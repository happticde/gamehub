using System.Buffers.Binary;
using System.Security.Cryptography;
using GameHub.Ingest.Data;
using Microsoft.Extensions.Configuration;

namespace GameHub.Ingest.Services;

/// <summary>Versioned AES-256 keys used to encrypt tenant platform credentials.</summary>
public sealed class PlatformCredentialKeyRing : IDisposable
{
    private const int RequiredKeySizeBytes = 32;
    private readonly Dictionary<int, byte[]> _keys;
    private bool _disposed;

    private PlatformCredentialKeyRing(int activeKeyVersion, Dictionary<int, byte[]> keys)
    {
        ActiveKeyVersion = activeKeyVersion;
        _keys = keys;
    }

    /// <summary>The key version used for newly encrypted credentials.</summary>
    public int ActiveKeyVersion { get; }

    /// <summary>Whether at least one valid encryption key is configured.</summary>
    public bool IsConfigured => !_disposed && _keys.Count > 0;

    /// <summary>Builds and validates the key ring from the Credentials configuration section.</summary>
    public static PlatformCredentialKeyRing FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("Credentials");
        var activeKeyVersion = section.GetValue<int?>("ActiveKeyVersion") ?? 1;
        if (activeKeyVersion <= 0)
        {
            throw new InvalidOperationException(
                "Credentials:ActiveKeyVersion must be a positive integer.");
        }

        var keys = new Dictionary<int, byte[]>();
        try
        {
            foreach (var child in section.GetSection("Keys").GetChildren())
            {
                if (string.IsNullOrWhiteSpace(child.Value))
                {
                    continue;
                }

                if (!int.TryParse(child.Key, out var keyVersion) || keyVersion <= 0)
                {
                    throw new InvalidOperationException(
                        $"Credential key version '{child.Key}' must be a positive integer.");
                }

                byte[] key;
                try
                {
                    key = Convert.FromBase64String(child.Value);
                }
                catch (FormatException ex)
                {
                    throw new InvalidOperationException(
                        $"Credentials:Keys:{keyVersion} must be Base64 encoded.",
                        ex);
                }

                if (key.Length != RequiredKeySizeBytes)
                {
                    CryptographicOperations.ZeroMemory(key);
                    throw new InvalidOperationException(
                        $"Credentials:Keys:{keyVersion} must decode to exactly 32 bytes.");
                }

                keys.Add(keyVersion, key);
            }

            if (keys.Count > 0 && !AesGcm.IsSupported)
            {
                throw new PlatformNotSupportedException(
                    "AES-GCM is unavailable on this platform.");
            }
            if (keys.Count > 0 && !keys.ContainsKey(activeKeyVersion))
            {
                throw new InvalidOperationException(
                    $"Credentials:ActiveKeyVersion {activeKeyVersion} has no configured key.");
            }

            return new PlatformCredentialKeyRing(activeKeyVersion, keys);
        }
        catch
        {
            foreach (var key in keys.Values)
            {
                CryptographicOperations.ZeroMemory(key);
            }
            throw;
        }
    }

    /// <summary>Clears decoded key material when the application service provider shuts down.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        foreach (var key in _keys.Values)
        {
            CryptographicOperations.ZeroMemory(key);
        }
        _keys.Clear();
        _disposed = true;
    }

    internal byte[] GetRequiredKey(int keyVersion)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsConfigured)
        {
            throw new PlatformCredentialVaultNotConfiguredException();
        }

        if (!_keys.TryGetValue(keyVersion, out var key))
        {
            throw new InvalidOperationException(
                $"Credential encryption key version {keyVersion} is unavailable.");
        }

        return key;
    }
}

/// <summary>Encrypts and authenticates credential payloads using the configured key ring.</summary>
public sealed class PlatformCredentialProtector(PlatformCredentialKeyRing keyRing)
{
    /// <summary>The current database encryption-envelope format.</summary>
    public const int CurrentEncryptionFormatVersion = 1;

    /// <summary>The NIST-recommended 96-bit GCM nonce size.</summary>
    public const int NonceSizeBytes = 12;

    /// <summary>The full 128-bit GCM authentication-tag size.</summary>
    public const int AuthenticationTagSizeBytes = 16;

    /// <summary>Encrypts a payload and writes the protected fields to the credential entity.</summary>
    public void Protect(PlatformCredential credential, ReadOnlySpan<byte> plaintext)
    {
        if (credential.TenantId == Guid.Empty)
        {
            throw new ArgumentException("Credential tenant identifier cannot be empty.", nameof(credential));
        }
        if (credential.CredentialSchemaVersion <= 0)
        {
            throw new ArgumentException("Credential schema version must be positive.", nameof(credential));
        }
        if (!Enum.IsDefined(credential.Platform))
        {
            throw new ArgumentException("Credential platform is unsupported.", nameof(credential));
        }
        if (plaintext.IsEmpty)
        {
            throw new ArgumentException("Credential payload cannot be empty.", nameof(plaintext));
        }

        var keyVersion = keyRing.ActiveKeyVersion;
        var key = keyRing.GetRequiredKey(keyVersion);
        var nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[AuthenticationTagSizeBytes];
        var associatedData = BuildAssociatedData(
            credential.TenantId,
            credential.Platform,
            credential.CredentialSchemaVersion,
            CurrentEncryptionFormatVersion,
            keyVersion);

        using var aes = new AesGcm(key, AuthenticationTagSizeBytes);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);

        credential.EncryptionFormatVersion = CurrentEncryptionFormatVersion;
        credential.EncryptionKeyVersion = keyVersion;
        credential.EncryptedPayload = ciphertext;
        credential.Nonce = nonce;
        credential.AuthenticationTag = tag;
    }

    /// <summary>Authenticates and decrypts one stored credential payload.</summary>
    public byte[] Unprotect(PlatformCredential credential)
    {
        if (credential.EncryptionFormatVersion != CurrentEncryptionFormatVersion)
        {
            throw new CryptographicException(
                $"Unsupported credential encryption format {credential.EncryptionFormatVersion}.");
        }
        if (credential.Nonce.Length != NonceSizeBytes ||
            credential.AuthenticationTag.Length != AuthenticationTagSizeBytes ||
            credential.EncryptedPayload.Length == 0)
        {
            throw new CryptographicException("Stored credential encryption fields are invalid.");
        }

        var key = keyRing.GetRequiredKey(credential.EncryptionKeyVersion);
        var plaintext = new byte[credential.EncryptedPayload.Length];
        var associatedData = BuildAssociatedData(
            credential.TenantId,
            credential.Platform,
            credential.CredentialSchemaVersion,
            credential.EncryptionFormatVersion,
            credential.EncryptionKeyVersion);

        try
        {
            using var aes = new AesGcm(key, AuthenticationTagSizeBytes);
            aes.Decrypt(
                credential.Nonce,
                credential.EncryptedPayload,
                credential.AuthenticationTag,
                plaintext,
                associatedData);
            return plaintext;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw;
        }
    }

    private static byte[] BuildAssociatedData(
        Guid tenantId,
        GameHub.Shared.Platform platform,
        int credentialSchemaVersion,
        int encryptionFormatVersion,
        int keyVersion)
    {
        var associatedData = new byte[32];
        tenantId.TryWriteBytes(associatedData);
        BinaryPrimitives.WriteInt32LittleEndian(associatedData.AsSpan(16, 4), (int)platform);
        BinaryPrimitives.WriteInt32LittleEndian(
            associatedData.AsSpan(20, 4),
            credentialSchemaVersion);
        BinaryPrimitives.WriteInt32LittleEndian(
            associatedData.AsSpan(24, 4),
            encryptionFormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(associatedData.AsSpan(28, 4), keyVersion);
        return associatedData;
    }
}

/// <summary>Raised when a credential operation requires an unconfigured encryption key ring.</summary>
public sealed class PlatformCredentialVaultNotConfiguredException : InvalidOperationException
{
    public PlatformCredentialVaultNotConfiguredException()
        : base("The platform credential vault is not configured.")
    {
    }
}
