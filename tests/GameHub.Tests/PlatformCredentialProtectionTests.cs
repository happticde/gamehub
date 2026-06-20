using System.Security.Cryptography;
using System.Text;
using GameHub.Ingest.Data;
using GameHub.Ingest.Services;
using GameHub.Shared;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace GameHub.Tests;

/// <summary>Cryptographic contract tests for the tenant credential vault.</summary>
public sealed class PlatformCredentialProtectionTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public void Protect_ValidPayload_RoundTripsWithoutStoringPlaintext()
    {
        var protector = CreateProtector(1, (1, 0x11));
        var credential = CreateCredential(TenantA);
        var plaintext = Encoding.UTF8.GetBytes("{\"apiKey\":\"top-secret\"}");

        protector.Protect(credential, plaintext);
        var decrypted = protector.Unprotect(credential);

        Assert.Equal(plaintext, decrypted);
        Assert.NotEqual(plaintext, credential.EncryptedPayload);
        Assert.Equal(PlatformCredentialProtector.NonceSizeBytes, credential.Nonce.Length);
        Assert.Equal(
            PlatformCredentialProtector.AuthenticationTagSizeBytes,
            credential.AuthenticationTag.Length);
        CryptographicOperations.ZeroMemory(plaintext);
        CryptographicOperations.ZeroMemory(decrypted);
    }

    [Fact]
    public void Protect_SamePayloadTwice_UsesDifferentNoncesAndCiphertext()
    {
        var protector = CreateProtector(1, (1, 0x22));
        var first = CreateCredential(TenantA);
        var second = CreateCredential(TenantA);
        var plaintext = Encoding.UTF8.GetBytes("{\"cookie\":\"secret\"}");

        protector.Protect(first, plaintext);
        protector.Protect(second, plaintext);

        Assert.NotEqual(first.Nonce, second.Nonce);
        Assert.NotEqual(first.EncryptedPayload, second.EncryptedPayload);
        CryptographicOperations.ZeroMemory(plaintext);
    }

    [Fact]
    public void Unprotect_CredentialMovedToDifferentTenant_RejectsTampering()
    {
        var protector = CreateProtector(1, (1, 0x33));
        var credential = CreateCredential(TenantA);
        var plaintext = Encoding.UTF8.GetBytes("{\"token\":\"secret\"}");
        protector.Protect(credential, plaintext);
        credential.TenantId = TenantB;

        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(credential));
        CryptographicOperations.ZeroMemory(plaintext);
    }

    [Fact]
    public void Unprotect_PreviousKeyVersion_RetainedKeyStillDecryptsAfterRotation()
    {
        var originalProtector = CreateProtector(1, (1, 0x44));
        var credential = CreateCredential(TenantA);
        var plaintext = Encoding.UTF8.GetBytes("{\"token\":\"rotatable\"}");
        originalProtector.Protect(credential, plaintext);
        var rotatedProtector = CreateProtector(2, (1, 0x44), (2, 0x55));

        var decrypted = rotatedProtector.Unprotect(credential);

        Assert.Equal(plaintext, decrypted);
        Assert.Equal(1, credential.EncryptionKeyVersion);
        CryptographicOperations.ZeroMemory(plaintext);
        CryptographicOperations.ZeroMemory(decrypted);
    }

    [Fact]
    public void FromConfiguration_InvalidKeyLength_FailsFast()
    {
        var configuration = BuildConfiguration(1, (1, 0x66, 16));

        var exception = Assert.Throws<InvalidOperationException>(
            () => PlatformCredentialKeyRing.FromConfiguration(configuration));

        Assert.Contains("exactly 32 bytes", exception.Message);
    }

    [Fact]
    public void FromConfiguration_ActiveVersionWithoutKey_FailsFast()
    {
        var configuration = BuildConfiguration(2, (1, 0x77, 32));

        var exception = Assert.Throws<InvalidOperationException>(
            () => PlatformCredentialKeyRing.FromConfiguration(configuration));

        Assert.Contains("has no configured key", exception.Message);
    }

    [Fact]
    public void Protect_NoConfiguredKey_FailsClosed()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var protector = new PlatformCredentialProtector(
            PlatformCredentialKeyRing.FromConfiguration(configuration));
        var plaintext = Encoding.UTF8.GetBytes("{\"token\":\"secret\"}");

        Assert.Throws<PlatformCredentialVaultNotConfiguredException>(
            () => protector.Protect(CreateCredential(TenantA), plaintext));
        CryptographicOperations.ZeroMemory(plaintext);
    }

    private static PlatformCredential CreateCredential(Guid tenantId) => new()
    {
        TenantId = tenantId,
        Platform = Platform.Steam,
        CredentialSchemaVersion = 1
    };

    private static PlatformCredentialProtector CreateProtector(
        int activeKeyVersion,
        params (int Version, byte Fill)[] keys) =>
        new(PlatformCredentialKeyRing.FromConfiguration(
            BuildConfiguration(
                activeKeyVersion,
                keys.Select(key => (key.Version, key.Fill, 32)).ToArray())));

    private static IConfiguration BuildConfiguration(
        int activeKeyVersion,
        params (int Version, byte Fill, int Length)[] keys)
    {
        var values = new Dictionary<string, string?>
        {
            ["Credentials:ActiveKeyVersion"] = activeKeyVersion.ToString()
        };
        foreach (var key in keys)
        {
            values[$"Credentials:Keys:{key.Version}"] =
                Convert.ToBase64String(Enumerable.Repeat(key.Fill, key.Length).ToArray());
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
