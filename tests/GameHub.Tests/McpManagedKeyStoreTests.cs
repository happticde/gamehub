using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Runtime.Versioning;
using GameHub.Core.Services;
using Microsoft.AspNetCore.DataProtection;
using Xunit;

namespace GameHub.Tests;

[SupportedOSPlatform("linux")]
public sealed class McpManagedKeyStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "mcp-managed-test-" + Guid.NewGuid().ToString("N"));
    private string Core => Path.Combine(root, "core");
    private string Frontend => Path.Combine(root, "frontend");
    private const string Origin = "https://issuer.example";
    private McpManagedKeyStore Store => new(Core);

    [Fact]
    public void Setup_RepeatedAndReopened_PreservesKeysAndPayloads()
    {
        Store.Setup(Frontend, Origin, true);
        var first = Store.ReadState();
        string protectedValue;
        using (var keys = Store.LoadProtocolKeys())
            protectedValue = keys.Provider.CreateProtector("fixture").Protect("private fixture payload");
        Store.Setup(Frontend, Origin, true);
        var reopened = new McpManagedKeyStore(Core);
        Assert.Equal(first.Generation, reopened.ReadState().Generation);
        Assert.Equal(first.Certificates, reopened.ReadState().Certificates);
        using var restored = reopened.LoadProtocolKeys();
        Assert.Equal("private fixture payload", restored.Provider.CreateProtector("fixture").Unprotect(protectedValue));
        Assert.NotEqual(first.TransportKey, first.ConsentKey);
        Assert.Equal(3, first.Certificates.Length);
        Assert.DoesNotContain("Certificates", File.ReadAllText(Path.Combine(Frontend, "state.json")));
        Assert.All(Directory.EnumerateFiles(Core, "*", SearchOption.AllDirectories),
            path => Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path)));
    }

    [Fact]
    public void Renewal_ReloadAndRestart_PreserveHistoricalDecryptorsAndRing()
    {
        Store.Setup(Frontend, Origin, true);
        using var keys = Store.LoadProtocolKeys();
        var protector = keys.Provider.CreateProtector("fixture");
        var payload = protector.Protect("before renewal");
        var original = Store.ReadState();
        Assert.False(Store.Renew());
        Assert.True(Store.Renew(force: true));
        var renewed = Store.ReadState();
        Assert.Equal(original.Generation, renewed.Generation);
        Assert.Equal(6, renewed.Certificates.Length);
        McpManagedKeyStore.ValidatePair(Frontend, renewed);
        using (var replacement = Store.LoadProtocolKeys()) keys.Reload(replacement);
        Assert.Equal("before renewal", protector.Unprotect(payload));
        using var restart = Store.LoadProtocolKeys();
        Assert.Equal("before renewal", restart.Provider.CreateProtector("fixture").Unprotect(payload));
        Assert.Equal("ready", Store.Check().Code);
    }

    [Theory]
    [InlineData("ring")]
    [InlineData("certificate")]
    [InlineData("permissions")]
    [InlineData("partial")]
    [InlineData("symlink")]
    [InlineData("manifest")]
    public void Startup_DamagedInitializedStorage_FailsWithoutReplacement(string defect)
    {
        Store.Setup(Frontend, Origin, true);
        var before = File.ReadAllBytes(Path.Combine(Core, "state.json"));
        var state = Store.ReadState();
        var certificate = Path.Combine(Core, "certificates", state.Certificates[0].Id + ".pfx");
        switch (defect)
        {
            case "ring": foreach (var file in Directory.EnumerateFiles(Path.Combine(Core, "ring"))) File.Delete(file); break;
            case "certificate": File.WriteAllText(certificate, "corrupt"); break;
            case "permissions": File.SetUnixFileMode(certificate, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead); break;
            case "partial": File.WriteAllText(Path.Combine(Core, "pending"), ""); break;
            case "symlink": File.Move(certificate, certificate + ".original"); File.CreateSymbolicLink(certificate, certificate + ".original"); break;
            case "manifest": File.WriteAllText(Path.Combine(Core, "state.json"), "{}"); break;
        }
        Assert.ThrowsAny<Exception>(() => Store.LoadProtocolKeys());
        Assert.ThrowsAny<Exception>(() => Store.Setup(Frontend, Origin, true));
        if (defect != "manifest") Assert.Equal(before, File.ReadAllBytes(Path.Combine(Core, "state.json")));
        if (defect == "ring") Assert.Empty(Directory.EnumerateFiles(Path.Combine(Core, "ring")));
    }

    [Fact]
    public async Task Setup_ConcurrentCalls_OneGenerationOnly()
    {
        Directory.CreateDirectory(Core, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Directory.CreateDirectory(Frontend, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var start = new ManualResetEventSlim();
        var tasks = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            start.Wait();
            try { Store.Setup(Frontend, Origin, true); return "ready"; }
            catch (McpKeyStoreException failure) { return failure.Code; }
        })).ToArray();
        start.Set();
        var results = await Task.WhenAll(tasks);
        Assert.Contains("ready", results);
        Assert.All(results, result => Assert.Contains(result, new[] { "ready", "operation_in_progress" }));
        Assert.Equal(3, Store.ReadState().Certificates.Length);
    }

    [Fact]
    public void Setup_StaleLockOrIncompleteFrontend_RefusesFreshInitialization()
    {
        Directory.CreateDirectory(Core, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.WriteAllText(Path.Combine(Core, "operation.lock"), "");
        Assert.Equal("operation_in_progress", Assert.Throws<McpKeyStoreException>(() => Store.Setup(Frontend, Origin, true)).Code);
        File.Delete(Path.Combine(Core, "operation.lock"));
        File.WriteAllText(Path.Combine(Frontend, "interrupted"), "");
        Assert.Equal("incomplete_state", Assert.Throws<McpKeyStoreException>(() => Store.Setup(Frontend, Origin, true)).Code);
    }

    [Fact]
    public void Setup_OriginOrGenerationMismatch_RefusesReconfiguration()
    {
        Store.Setup(Frontend, Origin, true);
        Assert.Equal("configuration_conflict", Assert.Throws<McpKeyStoreException>(() => Store.Setup(Frontend, "https://other.example", true)).Code);
        var frontend = Path.Combine(Frontend, "state.json");
        File.WriteAllText(frontend, File.ReadAllText(frontend).Replace(Store.ReadState().Generation, Guid.NewGuid().ToString("N")));
        Assert.Equal("generation_mismatch", Assert.Throws<McpKeyStoreException>(() => Store.Setup(Frontend, Origin, true)).Code);
    }

    [Fact]
    public void Check_NearExpiryAndExpired_ReportsEarlyThenFailsClosed()
    {
        Store.Setup(Frontend, Origin, true);
        var near = new McpManagedKeyStore(Core, new FixedClock(DateTimeOffset.UtcNow.AddYears(2).AddDays(-30)));
        Assert.Equal("renewal_due", near.Check().Code);
        var expired = new McpManagedKeyStore(Core, new FixedClock(DateTimeOffset.UtcNow.AddYears(3)));
        Assert.Equal("expired_certificate", Assert.Throws<McpKeyStoreException>(() => expired.Renew()).Code);
        Assert.Equal(3, Store.ReadState().Certificates.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Adoption_EncryptedLegacyRing_PreservesPayloadWithoutChangingSource(bool separatePasswords)
    {
        var legacy = Path.Combine(root, "legacy");
        var ring = Path.Combine(legacy, "ring");
        Directory.CreateDirectory(ring, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var paths = new[] { "signing", "encryption", "protection" }.Select(purpose =>
        {
            using var rsa = RSA.Create(2048);
            using var certificate = new CertificateRequest("CN=" + purpose, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
                .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(40));
            var path = Path.Combine(legacy, purpose + ".pfx");
            File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx, separatePasswords ? purpose : ""));
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return path;
        }).ToArray();
        using var signing = X509CertificateLoader.LoadPkcs12FromFile(paths[0], separatePasswords ? "signing" : "");
        using var encryption = X509CertificateLoader.LoadPkcs12FromFile(paths[1], separatePasswords ? "encryption" : "");
        using var protection = X509CertificateLoader.LoadPkcs12FromFile(paths[2], separatePasswords ? "protection" : "");
        string payload;
        using (var keys = new McpProtocolKeys(new DirectoryInfo(ring), signing, encryption, protection))
            payload = keys.Provider.CreateProtector("fixture").Protect("existing grant fixture");
        foreach (var path in Directory.EnumerateFiles(ring)) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var source = Directory.EnumerateFiles(legacy, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
        Store.Setup(Frontend, Origin, true, new(Origin, paths[0], paths[1], paths[2], ring, new string('x', 64), "", [],
            separatePasswords ? "signing" : null, separatePasswords ? "encryption" : null, separatePasswords ? "protection" : null));
        Assert.Equal("renewal_due", Store.Check().Code);
        Assert.True(Store.Renew());
        using var adopted = Store.LoadProtocolKeys();
        Assert.Equal("existing grant fixture", adopted.Provider.CreateProtector("fixture").Unprotect(payload));
        foreach (var (path, bytes) in source) Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void Commands_InvalidStorage_EmitsOnlyStableCode()
    {
        using var output = new StringWriter();
        Assert.Equal(1, McpKeyCommands.Run(["check", "--core-directory", Path.Combine(root, "missing-private-path")], output));
        Assert.DoesNotContain(root, output.ToString());
        Assert.DoesNotContain("Exception", output.ToString());
        Assert.True(JsonDocument.Parse(output.ToString()).RootElement.TryGetProperty("Code", out _));
    }

    [Theory]
    [InlineData("wrong_origin")]
    [InlineData("wrong_protection")]
    [InlineData("plaintext_ring")]
    [InlineData("unsafe_permissions")]
    public void Adoption_InvalidLegacyInput_RefusesImportWithoutChangingSource(string defect)
    {
        var sourceDirectory = Path.Combine(root, "source");
        var source = new McpManagedKeyStore(sourceDirectory);
        source.Setup(Path.Combine(root, "source-frontend"), Origin, true);
        var state = source.ReadState();
        string Certificate(string purpose) => Path.Combine(sourceDirectory, "certificates", state.Certificates.Last(entry => entry.Purpose == purpose).Id + ".pfx");
        var descriptor = new McpLegacyKeyImport(Origin, Certificate("signing"), Certificate("encryption"), Certificate("protection"),
            Path.Combine(sourceDirectory, "ring"), state.ConsentKey, "", []);
        switch (defect)
        {
            case "wrong_origin": descriptor = descriptor with { PublicOrigin = "https://other.example" }; break;
            case "wrong_protection": descriptor = descriptor with { ProtectionCertificate = Certificate("signing") }; break;
            case "plaintext_ring":
                foreach (var file in Directory.EnumerateFiles(descriptor.RingDirectory)) File.WriteAllText(file, "<key />");
                break;
            case "unsafe_permissions": File.SetUnixFileMode(Certificate("signing"), UnixFileMode.UserRead | UnixFileMode.OtherRead); break;
        }
        var before = Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
        Assert.ThrowsAny<Exception>(() => Store.Setup(Frontend, Origin, true, descriptor));
        foreach (var (path, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.False(File.Exists(Path.Combine(Core, "state.json")));
    }

    [Fact]
    public void Maintenance_StorageFailure_BlocksCryptoUntilValidatedRecovery()
    {
        Store.Setup(Frontend, Origin, true);
        using var keys = Store.LoadProtocolKeys();
        var protector = keys.Provider.CreateProtector("fixture");
        var payload = protector.Protect("existing grant");
        var maintenance = new McpKeyMaintenance(Store, keys, new Microsoft.Extensions.Options.OptionsCache<OpenIddict.Server.OpenIddictServerOptions>(),
            new Microsoft.Extensions.Options.OptionsCache<OpenIddict.Validation.OpenIddictValidationOptions>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<McpKeyMaintenance>.Instance);
        maintenance.RunOnce();
        var marker = Path.Combine(Core, "pending");
        File.WriteAllText(marker, "");
        maintenance.RunOnce();
        var failure = Assert.Throws<CryptographicException>(() => protector.Unprotect(payload));
        Assert.IsType<McpKeyStoreException>(failure.InnerException);
        File.Delete(marker);
        maintenance.RunOnce();
        Assert.Equal("existing grant", protector.Unprotect(payload));
    }

    [Fact]
    public void Backup_RestoredToAnotherDirectory_PreservesProtectedPayloadAndPairing()
    {
        Store.Setup(Frontend, Origin, true);
        string payload;
        using (var keys = Store.LoadProtocolKeys()) payload = keys.Provider.CreateProtector("fixture").Protect("backup fixture");
        var backup = Path.Combine(root, "restored-core");
        var frontendBackup = Path.Combine(root, "restored-frontend");
        Copy(Core, backup);
        Copy(Frontend, frontendBackup);
        using var restored = new McpManagedKeyStore(backup).LoadProtocolKeys();
        Assert.Equal("backup fixture", restored.Provider.CreateProtector("fixture").Unprotect(payload));
        McpManagedKeyStore.ValidatePair(frontendBackup, new McpManagedKeyStore(backup).ReadState());

        static void Copy(string from, string to)
        {
            Directory.CreateDirectory(to, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            foreach (var path in Directory.EnumerateFiles(from)) File.Copy(path, Path.Combine(to, Path.GetFileName(path)));
            foreach (var path in Directory.EnumerateDirectories(from)) Copy(path, Path.Combine(to, Path.GetFileName(path)));
        }
    }

    [Fact]
    public void KeyRing_ConcurrentUncommittedWrite_PreservesExistingPayloadReads()
    {
        Store.Setup(Frontend, Origin, true);
        string payload;
        using (var keys = Store.LoadProtocolKeys()) payload = keys.Provider.CreateProtector("fixture").Protect("existing payload");
        var pending = Path.Combine(Core, "ring", "concurrent.xml.partial.tmp");
        File.WriteAllText(pending, "<incomplete");
        File.SetUnixFileMode(pending, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        using var reader = Store.LoadProtocolKeys();
        Assert.Equal("existing payload", reader.Provider.CreateProtector("fixture").Unprotect(payload));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
}
