using System.Security.Cryptography;
using System.Text;
using GameHub.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace GameHub.Core.Services;

/// <summary>
/// Applies one validated provider logout to the exact external identity mapping. The operation
/// atomically advances the account session epoch, revokes every refresh session, records a
/// tenant-owned activity entry, and stores a hashed replay marker.
/// </summary>
public sealed class OidcBackChannelLogoutService(
    CoreContext db,
    TenantExecutionContext tenantExecution,
    TimeProvider timeProvider,
    ILogger<OidcBackChannelLogoutService> logger)
{
    private static readonly TimeSpan ReplayMarkerLifetime = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Invalidates the account selected only by the validated issuer/subject pair. Unknown or
    /// already deleted identities are successful no-ops so the endpoint cannot enumerate users.
    /// </summary>
    public async Task<OidcBackChannelLogoutResult> ProcessAsync(
        ValidatedOidcLogoutToken token,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(token);
        var tokenHash = HashReplayIdentity(token.Issuer, token.JwtId);
        if (await db.ProcessedOidcLogoutTokens.AsNoTracking().AnyAsync(
                candidate => candidate.TokenHash == tokenHash,
                cancellationToken))
        {
            return OidcBackChannelLogoutResult.AlreadyProcessed;
        }

        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            // Expired markers can no longer be replayed through the validator's much shorter iat
            // window. Opportunistic cleanup keeps storage bounded without another background job.
            await db.ProcessedOidcLogoutTokens
                .Where(candidate => candidate.ExpiresAtUtc <= nowUtc)
                .ExecuteDeleteAsync(cancellationToken);

            db.ProcessedOidcLogoutTokens.Add(new ProcessedOidcLogoutToken
            {
                Id = Guid.NewGuid(),
                TokenHash = tokenHash,
                ProcessedAtUtc = nowUtc,
                ExpiresAtUtc = nowUtc.Add(ReplayMarkerLifetime)
            });

            var mapping = await db.ExternalIdentities.AsNoTracking()
                .Where(candidate => candidate.Issuer == token.Issuer
                    && candidate.Subject == token.Subject
                    && candidate.UserAccount != null
                    && candidate.TenantId == candidate.UserAccount.TenantId)
                .Select(candidate => new
                {
                    candidate.UserAccountId,
                    candidate.TenantId
                })
                .SingleOrDefaultAsync(cancellationToken);

            if (mapping is not null)
            {
                // The account epoch is checked during every ordinary bearer authentication.
                // Incrementing it and revoking refresh rows in this transaction closes both the
                // current access path and every future rotation path together.
                var updatedAccounts = await db.UserAccounts
                    .Where(account => account.Id == mapping.UserAccountId
                        && account.TenantId == mapping.TenantId)
                    .ExecuteUpdateAsync(
                        setters => setters.SetProperty(
                            account => account.SessionVersion,
                            account => account.SessionVersion + 1),
                        cancellationToken);
                if (updatedAccounts == 1)
                {
                    await db.RefreshTokenSessions
                        .Where(session => session.UserAccountId == mapping.UserAccountId
                            && session.RevokedAtUtc == null)
                        .ExecuteUpdateAsync(
                            setters => setters.SetProperty(session => session.RevokedAtUtc, nowUtc),
                            cancellationToken);

                    using var tenantScope = tenantExecution.BeginScope(mapping.TenantId);
                    db.ActivityLogs.Add(new ActivityLog
                    {
                        Timestamp = nowUtc,
                        EventType = "OidcBackChannelLogout",
                        Message = "All GameHub sessions were invalidated by the identity provider.",
                        Details = null
                    });
                    await db.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    logger.LogInformation("Processed OIDC Back-Channel Logout for a mapped account");
                    return OidcBackChannelLogoutResult.Invalidated;
                }
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            logger.LogInformation("Processed OIDC Back-Channel Logout without a live mapping");
            return OidcBackChannelLogoutResult.NoLiveMapping;
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            if (await db.ProcessedOidcLogoutTokens.AsNoTracking().AnyAsync(
                    candidate => candidate.TokenHash == tokenHash,
                    cancellationToken))
            {
                return OidcBackChannelLogoutResult.AlreadyProcessed;
            }
            throw;
        }
    }

    private static string HashReplayIdentity(string issuer, string jwtId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{issuer}\n{jwtId}")));
}

/// <summary>Secret-free durable outcome categories for one accepted provider logout.</summary>
public enum OidcBackChannelLogoutResult
{
    Invalidated,
    AlreadyProcessed,
    NoLiveMapping
}
