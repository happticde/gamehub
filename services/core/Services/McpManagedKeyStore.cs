using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;

namespace GameHub.Core.Services;

/// <summary>Versioned durable state. Certificate names are constrained identifiers, never input paths.</summary>
public sealed record McpManagedState(int Version, string Generation, string PublicOrigin, bool Enabled,
    string TransportKey, string ConsentKey, McpManagedCertificate[] Certificates, string RingProof)
{
    /// <summary>Never formats internal credentials for diagnostics.</summary>
    public override string ToString() => "MCP managed state";
}

/// <summary>One retained key certificate; old decryptors are never deleted automatically.</summary>
public sealed record McpManagedCertificate(string Id, string Purpose, string Sha256);

/// <summary>Safe operator status independent of accounts, tenants and token contents.</summary>
public sealed record McpKeyReadiness(string Code, int DaysRemaining);

/// <summary>Explicit Linux provisioning, adoption and conservative renewal of independent protocol keys.</summary>
public sealed class McpManagedKeyStore(string coreDirectory, TimeProvider? clock = null)
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private static readonly string[] Purposes = ["signing", "encryption", "protection"];
    private const string Proof = "GameHub.Mcp.ManagedKeyStore.v1";
    private string StatePath => Path.Combine(coreDirectory, "state.json");
    private string Ring => Path.Combine(coreDirectory, "ring");
    private string Certificates => Path.Combine(coreDirectory, "certificates");

    /// <summary>Initializes empty dedicated stores, or validates an identical completed setup.</summary>
    public void Setup(string frontendDirectory, string origin, bool enabled, McpLegacyKeyImport? legacy = null)
    {
        ValidateOrigin(origin);
        if (!enabled || Path.GetFullPath(coreDirectory) == Path.GetFullPath(frontendDirectory)
            || Path.GetFullPath(frontendDirectory).StartsWith(Path.GetFullPath(coreDirectory) + "/", StringComparison.Ordinal)
            || Path.GetFullPath(coreDirectory).StartsWith(Path.GetFullPath(frontendDirectory) + "/", StringComparison.Ordinal))
            throw new McpKeyStoreException("invalid_setup");
        McpPrivateFiles.Directory(coreDirectory, create: true);
        McpPrivateFiles.Directory(frontendDirectory, create: true);
        using var lease = McpPrivateFiles.Lock(coreDirectory);
        using var frontendLease = McpPrivateFiles.Lock(frontendDirectory);
        if (File.Exists(StatePath))
        {
            if (legacy is not null) throw new McpKeyStoreException("already_initialized");
            var existing = ReadState();
            ValidatePair(frontendDirectory, existing);
            if (existing.PublicOrigin != origin || existing.Enabled != enabled) throw new McpKeyStoreException("configuration_conflict");
            using var keys = LoadProtocolKeys();
            return;
        }
        if (Directory.EnumerateFileSystemEntries(coreDirectory).Any(path => Path.GetFileName(path) != "operation.lock")
            || Directory.EnumerateFileSystemEntries(frontendDirectory).Any(path => Path.GetFileName(path) != "operation.lock"))
            throw new McpKeyStoreException("incomplete_state");
        McpPrivateFiles.Write(Path.Combine(coreDirectory, "pending"), []);
        McpPrivateFiles.Directory(Certificates, create: true);
        McpPrivateFiles.Directory(Ring, create: true);
        var entries = legacy is null ? Purposes.Select(CreateCertificate).ToArray() : Import(legacy, origin);
        var state = new McpManagedState(1, Guid.NewGuid().ToString("N"), origin, true,
            Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            legacy?.ConsentKey is { } key ? ValidateSecret(key) : Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), entries, "");
        ValidateCertificates(state);
        using (var keys = BuildProtocolKeys(state))
        {
            // Persist a proof under the real application discriminator. Losing the ring must
            // fail startup, instead of silently making existing protocol payloads unreadable.
            state = state with { RingProof = keys.Provider.CreateProtector(Proof).Protect(Proof) };
        }
        WriteState(frontendDirectory, state);
        McpPrivateFiles.Delete(Path.Combine(coreDirectory, "pending"));
    }

    /// <summary>Loads only a complete store and verifies its persistent ring before use.</summary>
    public McpProtocolKeys LoadProtocolKeys()
    {
        var state = ReadState();
        ValidateRing(state);
        var keys = BuildProtocolKeys(state);
        try
        {
            if (keys.Provider.CreateProtector(Proof).Unprotect(state.RingProof) != Proof)
                throw new McpKeyStoreException("invalid_ring");
            return keys;
        }
        catch { keys.Dispose(); throw new McpKeyStoreException("invalid_ring"); }
    }

    /// <summary>Validates configuration, certificates, file permissions, integrity and ring continuity.</summary>
    public McpManagedState ReadState()
    {
        McpPrivateFiles.Directory(coreDirectory);
        if (File.Exists(Path.Combine(coreDirectory, "pending"))) throw new McpKeyStoreException("incomplete_state");
        var state = JsonSerializer.Deserialize<McpManagedState>(McpPrivateFiles.Read(StatePath, 131072))
            ?? throw new McpKeyStoreException("invalid_state");
        if (state.Version != 1 || !Guid.TryParseExact(state.Generation, "N", out _) || !state.Enabled
            || state.Certificates is not { Length: >= 3 and <= 300 } || string.IsNullOrWhiteSpace(state.RingProof))
            throw new McpKeyStoreException("invalid_state");
        ValidateOrigin(state.PublicOrigin);
        ValidateSecret(state.TransportKey);
        ValidateSecret(state.ConsentKey);
        if (state.TransportKey == state.ConsentKey) throw new McpKeyStoreException("invalid_state");
        McpPrivateFiles.Directory(Ring);
        McpPrivateFiles.Directory(Certificates);
        ValidateCertificates(state);
        var allowed = new[] { "state.json", "operation.lock", "ring", "certificates" };
        if (Directory.EnumerateFileSystemEntries(coreDirectory).Any(path => !allowed.Contains(Path.GetFileName(path)))
            || Directory.EnumerateFileSystemEntries(Certificates).Any(path =>
                !state.Certificates.Any(entry => Path.GetFileName(path) == entry.Id + ".pfx")))
            throw new McpKeyStoreException("unexpected_state");
        return state;
    }

    /// <summary>Reports impending expiry without disclosing configured values or identities.</summary>
    public McpKeyReadiness Check()
    {
        var state = ReadState();
        using var keys = LoadProtocolKeys();
        var remaining = Purposes.Select(purpose =>
        {
            using var certificate = Load(state.Certificates.Last(entry => entry.Purpose == purpose));
            return (certificate.NotAfter.ToUniversalTime() - time.GetUtcNow().UtcDateTime).TotalDays;
        }).Min();
        return new(remaining <= 60 ? "renewal_due" : "ready", (int)Math.Floor(remaining));
    }

    /// <summary>Renews near expiry. Explicit force permits a tested operator maintenance window.</summary>
    public bool Renew(bool force = false)
    {
        McpPrivateFiles.Directory(coreDirectory);
        using var lease = McpPrivateFiles.Lock(coreDirectory);
        var state = ReadState();
        using var current = LoadProtocolKeys();
        if (!force && Check().Code != "renewal_due") return false;
        if (state.Certificates.Length > 297) throw new McpKeyStoreException("retention_limit");
        McpPrivateFiles.Write(Path.Combine(coreDirectory, "pending"), []);
        var renewed = state with { Certificates = [.. state.Certificates, .. Purposes.Select(CreateCertificate)] };
        ValidateCertificates(renewed);
        using (var replacement = BuildProtocolKeys(renewed))
            if (replacement.Provider.CreateProtector(Proof).Unprotect(state.RingProof) != Proof)
                throw new McpKeyStoreException("invalid_ring");
        // Trust pairing doesn't change during certificate renewal. The old ring and all old
        // decryptors remain; no claims of rewrapping/deleting historical protection keys.
        McpPrivateFiles.Write(StatePath, JsonSerializer.SerializeToUtf8Bytes(renewed), replace: true);
        McpPrivateFiles.Delete(Path.Combine(coreDirectory, "pending"));
        return true;
    }

    /// <summary>Requires identical generation, origin and internal credentials in both stores.</summary>
    public static void ValidatePair(string frontendDirectory, McpManagedState state)
    {
        McpPrivateFiles.Directory(frontendDirectory);
        if (Directory.EnumerateFileSystemEntries(frontendDirectory).Any(path => Path.GetFileName(path) is not ("state.json" or "operation.lock")))
            throw new McpKeyStoreException("unexpected_state");
        var frontend = JsonSerializer.Deserialize<McpFrontendKeyState>(McpPrivateFiles.Read(Path.Combine(frontendDirectory, "state.json"), 4096));
        if (frontend != new McpFrontendKeyState(state.Version, state.Generation, state.PublicOrigin, state.Enabled, state.TransportKey, state.ConsentKey))
            throw new McpKeyStoreException("generation_mismatch");
    }

    private void WriteState(string frontendDirectory, McpManagedState state)
    {
        McpPrivateFiles.Write(Path.Combine(frontendDirectory, "state.json"), JsonSerializer.SerializeToUtf8Bytes(
            new McpFrontendKeyState(state.Version, state.Generation, state.PublicOrigin, state.Enabled, state.TransportKey, state.ConsentKey)));
        McpPrivateFiles.Write(StatePath, JsonSerializer.SerializeToUtf8Bytes(state));
    }

    private McpManagedCertificate CreateCertificate(string purpose)
    {
        using var rsa = RSA.Create(3072);
        var request = new CertificateRequest("CN=GameHub MCP " + purpose, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(purpose == "signing"
            ? X509KeyUsageFlags.DigitalSignature : X509KeyUsageFlags.KeyEncipherment, critical: true));
        using var certificate = request.CreateSelfSigned(time.GetUtcNow().AddMinutes(-5), time.GetUtcNow().AddYears(2));
        return Save(purpose, certificate);
    }

    private McpManagedCertificate Save(string purpose, X509Certificate2 certificate)
    {
        var bytes = certificate.Export(X509ContentType.Pfx, "");
        var entry = new McpManagedCertificate(Guid.NewGuid().ToString("N"), purpose, Convert.ToHexString(SHA256.HashData(bytes)));
        McpPrivateFiles.Write(Path.Combine(Certificates, entry.Id + ".pfx"), bytes);
        return entry;
    }

    private X509Certificate2 Load(McpManagedCertificate entry)
    {
        if (!Guid.TryParseExact(entry.Id, "N", out _) || !Purposes.Contains(entry.Purpose)) throw new McpKeyStoreException("invalid_certificate");
        var bytes = McpPrivateFiles.Read(Path.Combine(Certificates, entry.Id + ".pfx"), 32768);
        if (entry.Sha256 != Convert.ToHexString(SHA256.HashData(bytes))) throw new McpKeyStoreException("invalid_certificate");
        return X509CertificateLoader.LoadPkcs12(bytes, "", X509KeyStorageFlags.EphemeralKeySet);
    }

    private void ValidateCertificates(McpManagedState state)
    {
        var publicKeys = new Dictionary<string, string>();
        foreach (var entry in state.Certificates)
        {
            using var certificate = Load(entry);
            using var rsa = certificate.GetRSAPrivateKey();
            if (rsa is not { KeySize: >= 2048 }) throw new McpKeyStoreException("invalid_certificate");
            var identity = Convert.ToHexString(SHA256.HashData(certificate.GetPublicKey()));
            if (publicKeys.TryGetValue(identity, out var purpose) && purpose != entry.Purpose)
                throw new McpKeyStoreException("shared_certificate");
            publicKeys[identity] = entry.Purpose;
        }
        if (state.Certificates.Select(entry => entry.Id).Distinct().Count() != state.Certificates.Length)
            throw new McpKeyStoreException("invalid_certificate");
        foreach (var purpose in Purposes)
        {
            var active = state.Certificates.LastOrDefault(entry => entry.Purpose == purpose)
                ?? throw new McpKeyStoreException("invalid_certificate");
            using var certificate = Load(active);
            if (certificate.NotBefore.ToUniversalTime() > time.GetUtcNow().UtcDateTime
                || certificate.NotAfter.ToUniversalTime() <= time.GetUtcNow().UtcDateTime)
                throw new McpKeyStoreException("expired_certificate");
        }
    }

    private McpProtocolKeys BuildProtocolKeys(McpManagedState state)
    {
        X509Certificate2[] For(string purpose) => state.Certificates.Where(entry => entry.Purpose == purpose).Select(Load).ToArray();
        return McpProtocolKeys.Managed(new DirectoryInfo(Ring), For("signing"), For("encryption"), For("protection"));
    }

    private McpManagedCertificate[] Import(McpLegacyKeyImport legacy, string origin)
    {
        if (legacy.PublicOrigin != origin) throw new McpKeyStoreException("configuration_conflict");
        var result = new List<McpManagedCertificate>();
        foreach (var (purpose, paths) in new[] { ("signing", new[] { legacy.SigningCertificate }),
                     ("encryption", new[] { legacy.EncryptionCertificate }),
                     ("protection", new[] { legacy.ProtectionCertificate }.Concat(legacy.RetiredProtectionCertificates).ToArray()) })
        {
            foreach (var path in paths.Reverse())
            {
                // Inputs are dedicated private files. Do not change their permissions or contents.
                var password = purpose switch
                {
                    "signing" => legacy.SigningCertificatePassword,
                    "encryption" => legacy.EncryptionCertificatePassword,
                    _ => legacy.ProtectionCertificatePassword
                };
                using var certificate = X509CertificateLoader.LoadPkcs12(McpPrivateFiles.Read(path, 32768),
                    password ?? legacy.CertificatePassword, X509KeyStorageFlags.EphemeralKeySet);
                result.Add(Save(purpose, certificate));
            }
        }
        McpPrivateFiles.Directory(legacy.RingDirectory);
        var elements = Directory.EnumerateFiles(legacy.RingDirectory).Select(path =>
        {
            if (!path.EndsWith(".xml", StringComparison.Ordinal)) throw new McpKeyStoreException("invalid_ring");
            var bytes = McpPrivateFiles.Read(path);
            var element = McpPrivateFiles.ReadElement(bytes);
            if (element.Name.LocalName != "revocation" && (element.Name.LocalName != "key"
                || !element.Descendants().Any(child => child.Name.LocalName == "encryptedSecret")))
                throw new McpKeyStoreException("unencrypted_ring");
            McpPrivateFiles.Write(Path.Combine(Ring, Guid.NewGuid().ToString("N") + ".xml"), bytes);
            return element;
        }).ToArray();
        if (elements.Length == 0) throw new McpKeyStoreException("invalid_ring");
        var imported = new McpManagedState(1, "", origin, true, "", "", result.ToArray(), "");
        ValidateCertificates(imported);
        ValidateRing(imported);
        return result.ToArray();
    }

    private void ValidateRing(McpManagedState state)
    {
        // Validate every historical descriptor before constructing a provider that is allowed
        // to generate keys. A lost/corrupt initialized ring must not be replaced on failure.
        var services = new ServiceCollection().AddDataProtection().SetApplicationName("GameHub.Mcp.Protocol.v1")
            .PersistKeysToFileSystem(new DirectoryInfo(Ring));
        services.Services.Configure<Microsoft.AspNetCore.DataProtection.KeyManagement.KeyManagementOptions>(options =>
            options.XmlRepository = new McpPrivateXmlRepository(Ring));
        using var certificates = new McpCertificateSet(state.Certificates.Where(entry => entry.Purpose == "protection").Select(Load).ToArray());
        services.UnprotectKeysWithAnyCertificate(certificates.Items);
        using var container = services.Services.BuildServiceProvider();
        var keys = container.GetRequiredService<Microsoft.AspNetCore.DataProtection.KeyManagement.IKeyManager>().GetAllKeys();
        if (keys.Count == 0) throw new McpKeyStoreException("invalid_ring");
        foreach (var key in keys)
            _ = key.CreateEncryptor() ?? throw new McpKeyStoreException("invalid_ring");
    }

    private static void ValidateOrigin(string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length > 0
            || uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0
            || uri.GetLeftPart(UriPartial.Authority) != origin) throw new McpKeyStoreException("invalid_origin");
    }

    private static string ValidateSecret(string value) => value is { Length: >= 32 and <= 512 }
        && value.All(character => character is >= '!' and <= '~') ? value : throw new McpKeyStoreException("invalid_secret");

    private sealed record McpFrontendKeyState(int Version, string Generation, string PublicOrigin, bool Enabled, string TransportKey, string ConsentKey);
    private sealed class McpCertificateSet(X509Certificate2[] items) : IDisposable
    {
        internal X509Certificate2[] Items => items;
        public void Dispose() { foreach (var item in items) item.Dispose(); }
    }
}

/// <summary>Explicit private-file import descriptor. The ring application name remains unchanged.</summary>
public sealed record McpLegacyKeyImport(string PublicOrigin, string SigningCertificate, string EncryptionCertificate,
    string ProtectionCertificate, string RingDirectory, string ConsentKey, string? CertificatePassword,
    string[] RetiredProtectionCertificates, string? SigningCertificatePassword = null,
    string? EncryptionCertificatePassword = null, string? ProtectionCertificatePassword = null)
{
    /// <summary>Never formats private import paths or credentials for diagnostics.</summary>
    public override string ToString() => "MCP private import";
}
