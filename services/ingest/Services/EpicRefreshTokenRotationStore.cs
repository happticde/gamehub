using System.Security.Cryptography;
using System.Text.Json;
using GameHub.Ingest.Data;
using GameHub.Ingest.Models;
using GameHub.Shared;
using Microsoft.EntityFrameworkCore;

namespace GameHub.Ingest.Services;

/// <summary>Persists Epic refresh-token rotation through the existing encrypted credential vault.</summary>
public interface IEpicRefreshTokenRotationStore
{
    /// <summary>Stores a rotated Epic refresh token for the active tenant when it changed.</summary>
    Task PersistIfRotatedAsync(
        EpicPlatformCredential currentCredential,
        EpicPlatformCredential refreshedCredential,
        CancellationToken cancellationToken = default);
}

/// <summary>Updates only the active tenant's Epic vault record after a successful refresh.</summary>
public sealed class EpicRefreshTokenRotationStore(
    IngestContext db,
    PlatformCredentialProtector protector,
    TimeProvider timeProvider) : IEpicRefreshTokenRotationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task PersistIfRotatedAsync(
        EpicPlatformCredential currentCredential,
        EpicPlatformCredential refreshedCredential,
        CancellationToken cancellationToken = default)
    {
        if (string.Equals(
                currentCredential.RefreshToken,
                refreshedCredential.RefreshToken,
                StringComparison.Ordinal))
        {
            return;
        }

        byte[]? existingPlaintext = null;
        byte[]? refreshedPlaintext = null;
        try
        {
            var credential = await db.PlatformCredentials
                .SingleOrDefaultAsync(item => item.Platform == Platform.Epic, cancellationToken);
            if (credential is null)
            {
                throw new PlatformCredentialRotationException();
            }
            if (credential.CredentialSchemaVersion != 1)
            {
                throw new PlatformCredentialRotationException();
            }

            existingPlaintext = protector.Unprotect(credential);
            var storedCredential = PlatformCredentialPayloadCodec.DecodeEpic(existingPlaintext);
            if (string.Equals(
                    storedCredential.RefreshToken,
                    refreshedCredential.RefreshToken,
                    StringComparison.Ordinal))
            {
                return;
            }
            if (!string.Equals(
                    storedCredential.RefreshToken,
                    currentCredential.RefreshToken,
                    StringComparison.Ordinal))
            {
                throw new PlatformCredentialRotationException();
            }

            refreshedPlaintext = JsonSerializer.SerializeToUtf8Bytes(refreshedCredential, JsonOptions);
            if (refreshedPlaintext.Length > PlatformCredentialService.MaximumPayloadSizeBytes)
            {
                throw new PlatformCredentialRotationException();
            }

            PlatformCredentialPayloadCodec.Validate(Platform.Epic, 1, refreshedPlaintext);
            credential.UpdatedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
            protector.Protect(credential, refreshedPlaintext);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (PlatformCredentialRotationException)
        {
            throw;
        }
        catch (PlatformCredentialVaultNotConfiguredException)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is CryptographicException or
                PlatformCredentialValidationException or
                InvalidOperationException or
                DbUpdateException)
        {
            throw new PlatformCredentialRotationException(ex);
        }
        finally
        {
            if (existingPlaintext is not null)
            {
                CryptographicOperations.ZeroMemory(existingPlaintext);
            }
            if (refreshedPlaintext is not null)
            {
                CryptographicOperations.ZeroMemory(refreshedPlaintext);
            }
        }
    }
}

/// <summary>Raised when Epic refresh-token rotation cannot be safely persisted.</summary>
public sealed class PlatformCredentialRotationException : InvalidOperationException
{
    private const string SanitizedMessage = "Epic refresh token rotation could not be persisted.";

    public PlatformCredentialRotationException()
        : base(SanitizedMessage)
    {
    }

    public PlatformCredentialRotationException(Exception innerException)
        : base(SanitizedMessage, innerException)
    {
    }
}
