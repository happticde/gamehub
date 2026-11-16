using System.Xml.Linq;
using System.Runtime.InteropServices;
using System.Xml;
using Microsoft.AspNetCore.DataProtection.Repositories;

namespace GameHub.Core.Services;

/// <summary>Private Linux storage shared by provisioning and the protocol key ring.</summary>
internal static class McpPrivateFiles
{
    internal const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    internal const UnixFileMode FileModeBits = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    internal static void PrepareRoot(string path)
    {
        if (!OperatingSystem.IsLinux()) throw new McpKeyStoreException("unsupported_storage");
        ValidatePath(path);
        // Fresh Docker volumes are root-owned 0755. Only an empty bootstrap volume may be
        // tightened automatically; initialized or populated unsafe storage is never repaired.
        if (GetEffectiveUser() == 0 && System.IO.Directory.Exists(path)
            && !System.IO.Directory.EnumerateFileSystemEntries(path).Any())
            System.IO.File.SetUnixFileMode(path, DirectoryMode);
        Directory(path, create: true);
    }

    internal static void AssignOwner(string path, uint owner)
    {
        if (GetEffectiveUser() != 0) throw new McpKeyStoreException("setup_requires_root");
        ValidatePath(path);
        foreach (var child in System.IO.Directory.EnumerateFileSystemEntries(path))
        {
            ValidatePath(child);
            if (System.IO.Directory.Exists(child)) AssignOwner(child, owner);
            else if (ChangeOwner(child, owner, owner) != 0) throw new McpKeyStoreException("ownership_failed");
        }
        if (ChangeOwner(path, owner, owner) != 0) throw new McpKeyStoreException("ownership_failed");
    }

    [DllImport("libc", EntryPoint = "geteuid")]
    internal static extern uint GetEffectiveUser();
    [DllImport("libc", EntryPoint = "chown", SetLastError = true)]
    private static extern int ChangeOwner(string path, uint owner, uint group);
    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenDirectory(string path, int flags);
    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int Sync(int descriptor);
    [DllImport("libc", EntryPoint = "close")]
    private static extern int Close(int descriptor);

    internal static void SyncDirectory(string path)
    {
        // Atomic rename alone doesn't make directory entries durable after power loss.
        // O_RDONLY (zero on Linux) also opens directories without architecture-specific flags.
        var descriptor = OpenDirectory(path, 0);
        if (descriptor < 0) throw new McpKeyStoreException("durability_failed");
        try { if (Sync(descriptor) != 0) throw new McpKeyStoreException("durability_failed"); }
        finally { Close(descriptor); }
    }

    internal static void Delete(string path)
    {
        ValidatePath(path);
        System.IO.File.Delete(path);
        SyncDirectory(Path.GetDirectoryName(path)!);
    }

    internal static void ValidatePath(string path)
    {
        if (!OperatingSystem.IsLinux() || !Path.IsPathFullyQualified(path)) throw new McpKeyStoreException("unsupported_storage");
        var current = Path.GetPathRoot(path)!;
        foreach (var part in path.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (new FileInfo(current).LinkTarget is not null || new DirectoryInfo(current).LinkTarget is not null)
                throw new McpKeyStoreException("unsafe_storage");
        }
    }

    internal static void Directory(string path, bool create = false)
    {
        if (!OperatingSystem.IsLinux()) throw new McpKeyStoreException("unsupported_storage");
        ValidatePath(path);
        if (create && !System.IO.Directory.Exists(path))
        {
            System.IO.Directory.CreateDirectory(path, DirectoryMode);
            SyncDirectory(Path.GetDirectoryName(path)!);
        }
        if (!System.IO.Directory.Exists(path) || System.IO.File.GetUnixFileMode(path) != DirectoryMode)
            throw new McpKeyStoreException("unsafe_directory");
    }

    internal static byte[] Read(string path, int maximum = 1_048_576)
    {
        if (!OperatingSystem.IsLinux()) throw new McpKeyStoreException("unsupported_storage");
        ValidatePath(path);
        if (!System.IO.File.Exists(path) || System.IO.File.GetUnixFileMode(path) != FileModeBits
            || new FileInfo(path).Length > maximum) throw new McpKeyStoreException("unsafe_file");
        return System.IO.File.ReadAllBytes(path);
    }

    internal static void Write(string path, byte[] bytes, bool replace = false)
    {
        if (!OperatingSystem.IsLinux()) throw new McpKeyStoreException("unsupported_storage");
        ValidatePath(path);
        Directory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var stream = new FileStream(temporary, new FileStreamOptions
               { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = FileModeBits }))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        System.IO.File.Move(temporary, path, replace);
        SyncDirectory(Path.GetDirectoryName(path)!);
    }

    internal static IDisposable Lock(string directory)
    {
        if (!OperatingSystem.IsLinux()) throw new McpKeyStoreException("unsupported_storage");
        var path = Path.Combine(directory, "operation.lock");
        ValidatePath(path);
        try
        {
            // Exclusive creation also excludes another thread in this process. An interrupted
            // operation deliberately leaves a lock: never guess that initialized state is fresh.
            var stream = new FileStream(path, new FileStreamOptions
                { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = FileModeBits });
            stream.Flush(flushToDisk: true);
            try { SyncDirectory(directory); }
            catch { stream.Dispose(); throw; }
            return new Lease(path, stream);
        }
        catch (IOException) { throw new McpKeyStoreException("operation_in_progress"); }
    }

    private sealed class Lease(string path, FileStream stream) : IDisposable
    {
        public void Dispose() { stream.Dispose(); Delete(path); }
    }

    internal static XElement ReadElement(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1_048_576 });
        return XElement.Load(reader);
    }
}

/// <summary>Stores only encrypted data-protection XML with restrictive creation permissions.</summary>
internal sealed class McpPrivateXmlRepository(string directory) : IXmlRepository
{
    public IReadOnlyCollection<XElement> GetAllElements()
    {
        McpPrivateFiles.Directory(directory);
        return System.IO.Directory.EnumerateFiles(directory).Select(path =>
        {
            if (!path.EndsWith(".xml", StringComparison.Ordinal)) throw new McpKeyStoreException("invalid_ring");
            return McpPrivateFiles.ReadElement(McpPrivateFiles.Read(path));
        }).ToArray();
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        // Framework-friendly names aren't paths. Random names avoid both traversal and overwrite.
        McpPrivateFiles.Write(Path.Combine(directory, Guid.NewGuid().ToString("N") + ".xml"),
            System.Text.Encoding.UTF8.GetBytes(element.ToString(SaveOptions.DisableFormatting)));
    }
}

/// <summary>A stable, secret-free failure code for operator diagnostics.</summary>
public sealed class McpKeyStoreException(string code) : Exception(code)
{
    /// <summary>Public diagnostic code; never contains paths or cryptographic material.</summary>
    public string Code { get; } = code;
}
