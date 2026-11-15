using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GameHub.Mcp.Authentication;

/// <summary>Permission-protected, unencrypted bearer credential. Never log or format its contents.</summary>
public sealed class McpCredential
{
    /// <summary>Storage schema version.</summary>
    public int Version { get; init; } = 1;
    /// <summary>Exact issuer binding.</summary>
    public required string Issuer { get; init; }
    /// <summary>Exact resource binding.</summary>
    public required string Resource { get; init; }
    /// <summary>Local connection binding.</summary>
    public required string Connection { get; init; }
    /// <summary>Opaque access credential.</summary>
    public required string AccessToken { get; init; }
    /// <summary>Opaque renewable credential.</summary>
    public required string RefreshToken { get; init; }
    /// <summary>Local access expiry, allowing downstream proactive renewal.</summary>
    public required DateTimeOffset AccessExpiresAt { get; init; }
    /// <summary>Permissions actually granted, as returned by the protocol.</summary>
    public required string Scope { get; init; }
}

/// <summary>Provides one exclusive read/refresh/write boundary per issuer, resource and connection.</summary>
public sealed class McpCredentialStore(McpLoginSettings settings)
{
    private const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode FileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const int MaximumBytes = 8192;
    private string ConnectionDirectory => Path.Combine(settings.Directory,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            settings.Issuer.AbsoluteUri + "\n" + settings.Resource + "\n" + settings.Connection))).ToLowerInvariant());

    /// <summary>Acquires an OS-held exclusive file lock. Never unlink the lock inode.</summary>
    public async Task<LockedCredential> LockAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux())
            throw new McpLoginException("Credential storage requires the Linux Docker image and its dedicated volume.");
        SecureDirectory(settings.Directory);
        SecureDirectory(ConnectionDirectory);
        var path = Path.Combine(ConnectionDirectory, "lock");
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CheckFile(path);
            try
            {
                var stream = new FileStream(path, new FileStreamOptions
                {
                    Mode = System.IO.FileMode.OpenOrCreate, Access = FileAccess.ReadWrite,
                    Share = FileShare.None, UnixCreateMode = FileMode
                });
                try { CheckFile(path); return new LockedCredential(this, stream); }
                catch { stream.Dispose(); throw; }
            }
            catch (IOException) when (elapsed.Elapsed < TimeSpan.FromSeconds(5))
            { await Task.Delay(50, cancellationToken); }
        }
    }

    private static void SecureDirectory(string path)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        // Refuse links in every existing ancestor, including operator-selected mount paths.
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
            if (current.LinkTarget is not null)
                throw new McpLoginException("Credential storage must not contain symbolic links.");
        System.IO.Directory.CreateDirectory(path, DirectoryMode);
        if (File.GetUnixFileMode(path) != DirectoryMode)
            throw new McpLoginException("Credential directories require mode 0700 and ownership by the non-root runtime user. Repair the dedicated volume permissions.");
    }

    private static void CheckFile(string path)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        var info = new FileInfo(path);
        if (info.LinkTarget is not null || (info.Exists && (info.Attributes.HasFlag(FileAttributes.Directory)
            || File.GetUnixFileMode(path) != FileMode)))
            throw new McpLoginException("Credential files require mode 0600 and must not be symbolic links.");
    }

    private void Validate(McpCredential value)
    {
        if (value.Version != 1 || value.Issuer != settings.Issuer.AbsoluteUri
            || value.Resource != settings.Resource || value.Connection != settings.Connection
            || !ValidToken(value.AccessToken) || !ValidToken(value.RefreshToken)
            || value.AccessExpiresAt == default || !ValidScope(value.Scope))
            throw new McpLoginException("Selected credential storage is corrupt or bound to another connection. Restore a trusted backup or revoke the connection in GameHub before removing its local credential.");
    }

    internal static bool ValidToken(string? value) => value is { Length: > 0 and <= 1024 }
        && value.All(character => character is >= '!' and <= '~');

    internal static bool ValidScope(string? value) => value is not null
        && value.Split(' ') is var scopes && scopes.Contains("library:read") && scopes.Contains("offline_access")
        && scopes.Distinct().Count() == scopes.Length
        && scopes.All(scope => scope is "library:read" or "tracking:write" or "offline_access");

    /// <summary>Holds the exclusive OS lock until disposed, including across remote renewal or logout.</summary>
    public sealed class LockedCredential : IDisposable
    {
        private readonly McpCredentialStore store;
        private readonly FileStream stream;
        private bool disposed;
        private string PathName => Path.Combine(store.ConnectionDirectory, "credential.json");
        internal LockedCredential(McpCredentialStore store, FileStream stream)
        { this.store = store; this.stream = stream; }

        /// <summary>Reads only the selected bounded, correctly permissioned credential.</summary>
        public async Task<McpCredential?> ReadAsync(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            CheckFile(PathName);
            if (!File.Exists(PathName)) return null;
            await using var input = new FileStream(PathName, System.IO.FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length > MaximumBytes) throw new McpLoginException("Selected credential storage is corrupt. Revoke the connection in GameHub before repairing storage.");
            try
            {
                using var document = await JsonDocument.ParseAsync(input,
                    new JsonDocumentOptions { MaxDepth = 4 }, cancellationToken);
                if (document.RootElement.ValueKind != JsonValueKind.Object
                    || document.RootElement.EnumerateObject().Select(property => property.Name).Distinct().Count()
                        != document.RootElement.EnumerateObject().Count()) throw new JsonException();
                var value = document.RootElement.Deserialize<McpCredential>(new JsonSerializerOptions
                {
                    MaxDepth = 4,
                    UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
                }) ?? throw new JsonException();
                store.Validate(value);
                return value;
            }
            catch (JsonException)
            { throw new McpLoginException("Selected credential storage is corrupt. Revoke the connection in GameHub before repairing storage."); }
        }

        /// <summary>Flushes a private temporary file before atomic replacement. An interrupted write preserves the previous credential.</summary>
        public async Task WriteAsync(McpCredential value, CancellationToken cancellationToken = default)
        {
            if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
            ObjectDisposedException.ThrowIf(disposed, this);
            store.Validate(value);
            CheckFile(PathName);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
            if (bytes.Length > MaximumBytes) throw new McpLoginException("Credential response exceeded the storage limit.");
            var temporary = Path.Combine(store.ConnectionDirectory, Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                await using (var output = new FileStream(temporary, new FileStreamOptions
                {
                    Mode = System.IO.FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                    UnixCreateMode = FileMode
                }))
                {
                    await output.WriteAsync(bytes, cancellationToken);
                    output.Flush(flushToDisk: true);
                }
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporary, PathName, overwrite: true);
            }
            finally { File.Delete(temporary); }
        }

        /// <summary>Removes only this selected credential while retaining the shared lock file.</summary>
        public void Delete()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            CheckFile(PathName);
            File.Delete(PathName);
        }

        /// <inheritdoc/>
        public void Dispose() { if (disposed) return; disposed = true; stream.Dispose(); }
    }
}
