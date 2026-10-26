using System.Security.Cryptography;
using System.Text.Json;
using GameHub.Ingest.Data;
using GameHub.Ingest.Models;
using GameHub.Shared;
using Microsoft.EntityFrameworkCore;

namespace GameHub.Ingest.Services;

/// <summary>Stores tenant platform credentials without exposing plaintext after the request.</summary>
public sealed class PlatformCredentialService(
    IngestContext db,
    IIngestTenantContext tenantContext,
    PlatformCredentialProtector protector,
    TimeProvider timeProvider,
    IEpicAuthenticationClient? epicAuthenticationClient = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Maximum serialized plaintext accepted by the vault.</summary>
    public const int MaximumPayloadSizeBytes = 16 * 1024;

    /// <summary>Upper bound reserved for future credential-schema evolution.</summary>
    public const int MaximumSchemaVersion = 100;

    /// <summary>Creates or replaces the active tenant's credential for one platform.</summary>
    public async Task<PlatformCredentialStatusResponse> UpsertAsync(
        Platform platform,
        int schemaVersion,
        JsonElement credentials,
        CancellationToken cancellationToken = default)
    {
        Validate(platform, schemaVersion, credentials);
        var plaintext = await BuildPlaintextAsync(platform, credentials, cancellationToken);
        try
        {
            if (plaintext.Length > MaximumPayloadSizeBytes)
            {
                throw new PlatformCredentialValidationException(
                    $"Credential payload must not exceed {MaximumPayloadSizeBytes} UTF-8 bytes.");
            }

            PlatformCredentialPayloadCodec.Validate(platform, schemaVersion, plaintext);

            var credential = await db.PlatformCredentials
                .SingleOrDefaultAsync(item => item.Platform == platform, cancellationToken);
            var now = timeProvider.GetUtcNow().UtcDateTime;
            if (credential is null)
            {
                credential = new PlatformCredential
                {
                    TenantId = tenantContext.TenantId,
                    Platform = platform,
                    CreatedAtUtc = now
                };
                db.PlatformCredentials.Add(credential);
            }

            credential.CredentialSchemaVersion = schemaVersion;
            credential.UpdatedAtUtc = now;
            protector.Protect(credential, plaintext);
            await db.SaveChangesAsync(cancellationToken);
            return ToStatus(credential);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>Lists secret-free configuration status for every supported platform.</summary>
    public async Task<IReadOnlyList<PlatformCredentialStatusResponse>> ListStatusAsync(
        CancellationToken cancellationToken = default)
    {
        var configured = await db.PlatformCredentials
            .AsNoTracking()
            .ToDictionaryAsync(item => item.Platform, cancellationToken);

        return Enum.GetValues<Platform>()
            .Select(platform => configured.TryGetValue(platform, out var credential)
                ? ToStatus(credential)
                : new PlatformCredentialStatusResponse(platform, false, null, null))
            .ToList();
    }

    /// <summary>Deletes the active tenant's credential for one platform.</summary>
    public async Task<bool> DeleteAsync(
        Platform platform,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(platform))
        {
            return false;
        }

        var credential = await db.PlatformCredentials
            .SingleOrDefaultAsync(item => item.Platform == platform, cancellationToken);
        if (credential is null)
        {
            return false;
        }

        db.PlatformCredentials.Remove(credential);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>Decrypts the active tenant's Steam credential for immediate retrieval.</summary>
    public Task<SteamPlatformCredential?> GetSteamAsync(CancellationToken cancellationToken = default) =>
        ReadAsync(Platform.Steam, PlatformCredentialPayloadCodec.DecodeSteam, cancellationToken);

    /// <summary>Decrypts the active tenant's GOG credential for immediate retrieval.</summary>
    public Task<GogPlatformCredential?> GetGogAsync(CancellationToken cancellationToken = default) =>
        ReadAsync(Platform.Gog, PlatformCredentialPayloadCodec.DecodeGog, cancellationToken);

    /// <summary>Decrypts the active tenant's PlayStation credential for immediate retrieval.</summary>
    public Task<PsnPlatformCredential?> GetPsnAsync(CancellationToken cancellationToken = default) =>
        ReadAsync(Platform.Psn, PlatformCredentialPayloadCodec.DecodePsn, cancellationToken);

    /// <summary>Decrypts the active tenant's Epic Games credential for immediate retrieval.</summary>
    public Task<EpicPlatformCredential?> GetEpicAsync(CancellationToken cancellationToken = default) =>
        ReadAsync(Platform.Epic, PlatformCredentialPayloadCodec.DecodeEpic, cancellationToken);

    private async Task<T?> ReadAsync<T>(
        Platform platform,
        Func<ReadOnlySpan<byte>, T> decode,
        CancellationToken cancellationToken)
        where T : class
    {
        var credential = await db.PlatformCredentials
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Platform == platform, cancellationToken);
        if (credential is null)
        {
            return null;
        }

        byte[]? plaintext = null;
        try
        {
            plaintext = protector.Unprotect(credential);
            if (credential.CredentialSchemaVersion != 1)
            {
                throw new PlatformCredentialValidationException(
                    $"Stored credential schema {credential.CredentialSchemaVersion} is unsupported.");
            }
            return decode(plaintext);
        }
        catch (PlatformCredentialVaultNotConfiguredException)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is CryptographicException or
                PlatformCredentialValidationException or
                InvalidOperationException)
        {
            throw new PlatformCredentialReadException(platform, ex);
        }
        finally
        {
            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    private static void Validate(
        Platform platform,
        int schemaVersion,
        JsonElement credentials)
    {
        if (!Enum.IsDefined(platform))
        {
            throw new PlatformCredentialValidationException("Platform is unsupported.");
        }
        if (schemaVersion is <= 0 or > MaximumSchemaVersion)
        {
            throw new PlatformCredentialValidationException(
                $"SchemaVersion must be between 1 and {MaximumSchemaVersion}.");
        }
        if (credentials.ValueKind != JsonValueKind.Object || !credentials.EnumerateObject().Any())
        {
            throw new PlatformCredentialValidationException(
                "Credentials must be a non-empty JSON object.");
        }
        PlatformCredentialPayloadCodec.ValidateSchemaVersion(platform, schemaVersion);
    }

    private async Task<byte[]> BuildPlaintextAsync(
        Platform platform,
        JsonElement credentials,
        CancellationToken cancellationToken)
    {
        if (platform != Platform.Epic)
        {
            return JsonSerializer.SerializeToUtf8Bytes(credentials);
        }

        var authorizationCode = PlatformCredentialPayloadCodec.ReadEpicAuthorizationCode(credentials);
        if (authorizationCode is null)
        {
            return JsonSerializer.SerializeToUtf8Bytes(credentials);
        }

        if (epicAuthenticationClient is null)
        {
            throw new PlatformCredentialValidationException(
                "Epic authorization code exchange is not configured.");
        }

        var credential = await epicAuthenticationClient.ExchangeAuthorizationCodeAsync(
            authorizationCode,
            cancellationToken);
        return JsonSerializer.SerializeToUtf8Bytes(credential, JsonOptions);
    }

    private static PlatformCredentialStatusResponse ToStatus(PlatformCredential credential) =>
        new(
            credential.Platform,
            true,
            credential.CredentialSchemaVersion,
            credential.UpdatedAtUtc);
}

/// <summary>Raised when a credential request violates the vault's public contract.</summary>
public sealed class PlatformCredentialValidationException : ArgumentException
{
    public PlatformCredentialValidationException(string message) : base(message)
    {
    }

    public PlatformCredentialValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Raised when stored credential material cannot be safely decoded.</summary>
public sealed class PlatformCredentialReadException(Platform platform, Exception innerException)
    : InvalidOperationException($"Stored credential for {platform} is unavailable.", innerException);
