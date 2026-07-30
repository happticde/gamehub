using System.Security.Cryptography;
using System.Text;
using GameHub.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace GameHub.Core.Services;

/// <summary>
/// Applies a completely validated OIDC logout to either one previously mapped local session
/// (<c>sid</c>) or every local session for an exact external identity (<c>sub</c> only).
/// </summary>
public sealed class OidcBackChannelLogoutService(
    CoreContext db,
    TenantExecutionContext tenantExecution,
    TimeProvider timeProvider,
    ILogger<OidcBackChannelLogoutService> logger)
{
    private static readonly TimeSpan ReplayMarkerLifetime = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Processes one provider message without exposing whether its identity or session exists.
    /// When both standard claims are present, a known session must belong to the exact subject
    /// mapping or the message is rejected without changing either account.
    /// </summary>
    public Task<OidcBackChannelLogoutResult> ProcessAsync(
        ValidatedOidcLogoutToken token,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(token);
        return ProcessCoreAsync(token, retryAfterUniqueRace: true, cancellationToken);
    }

    private async Task<OidcBackChannelLogoutResult> ProcessCoreAsync(
        ValidatedOidcLogoutToken token,
        bool retryAfterUniqueRace,
        CancellationToken cancellationToken)
    {
        var replayHash = HashReplayIdentity(token.Issuer, token.JwtId);
        if (await db.ProcessedOidcLogoutTokens.AsNoTracking().AnyAsync(
                candidate => candidate.TokenHash == replayHash,
                cancellationToken))
        {
            return OidcBackChannelLogoutResult.AlreadyProcessed;
        }

        var providerSessionHash = token.ProviderSessionId is null
            ? null
            : OidcProviderSessionKey.Hash(token.Issuer, token.ProviderSessionId);
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await db.ProcessedOidcLogoutTokens
                .Where(candidate => candidate.ExpiresAtUtc <= nowUtc)
                .ExecuteDeleteAsync(cancellationToken);

            var subjectMapping = token.Subject is null
                ? null
                : await db.ExternalIdentities.AsNoTracking()
                    .Where(candidate => candidate.Issuer == token.Issuer
                        && candidate.Subject == token.Subject
                        && candidate.UserAccount != null
                        && candidate.TenantId == candidate.UserAccount.TenantId)
                    .Select(candidate => new LogoutIdentityMapping(
                        candidate.Id,
                        candidate.UserAccountId,
                        candidate.TenantId))
                    .SingleOrDefaultAsync(cancellationToken);

            var providerSession = providerSessionHash is null
                ? null
                : await db.OidcProviderSessions.AsNoTracking()
                    .Where(candidate => candidate.SessionKeyHash == providerSessionHash)
                    .Select(candidate => new LogoutProviderSessionMapping(
                        candidate.Id,
                        candidate.ExternalIdentityId,
                        candidate.AccountSessionId,
                        candidate.UserAccountId,
                        candidate.TenantId,
                        candidate.ReissueBlockedAtUtc))
                    .SingleOrDefaultAsync(cancellationToken);

            if (token.Subject is not null
                && providerSession is
                {
                    ExternalIdentityId: not null,
                    AccountSessionId: not null,
                    UserAccountId: not null,
                    TenantId: not null
                }
                && (subjectMapping is null
                    || providerSession.ExternalIdentityId != subjectMapping.ExternalIdentityId
                    || providerSession.UserAccountId != subjectMapping.UserAccountId
                    || providerSession.TenantId != subjectMapping.TenantId))
            {
                await transaction.RollbackAsync(cancellationToken);
                logger.LogWarning(
                    "Rejected OIDC Back-Channel Logout because subject and session mappings disagree");
                return OidcBackChannelLogoutResult.SubjectSessionMismatch;
            }

            db.ProcessedOidcLogoutTokens.Add(new ProcessedOidcLogoutToken
            {
                Id = Guid.NewGuid(),
                TokenHash = replayHash,
                ProcessedAtUtc = nowUtc,
                ExpiresAtUtc = nowUtc.Add(ReplayMarkerLifetime)
            });

            OidcBackChannelLogoutResult result;
            if (providerSessionHash is not null)
            {
                result = await RevokeProviderSessionAsync(
                    providerSessionHash,
                    providerSession,
                    nowUtc,
                    cancellationToken);
            }
            else
            {
                result = await RevokeSubjectSessionsAsync(
                    subjectMapping,
                    nowUtc,
                    cancellationToken);
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            logger.LogInformation("Processed OIDC Back-Channel Logout");
            return result;
        }
        catch (DbUpdateException) when (retryAfterUniqueRace)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            if (await db.ProcessedOidcLogoutTokens.AsNoTracking().AnyAsync(
                    candidate => candidate.TokenHash == replayHash,
                    cancellationToken))
            {
                return OidcBackChannelLogoutResult.AlreadyProcessed;
            }

            // A validated login and logout can both be the first writer for one provider sid.
            // The issuer-bound unique hash is their shared ordering authority: retry after the
            // winner commits, then either revoke its mapping or observe its tombstone.
            return await ProcessCoreAsync(
                token,
                retryAfterUniqueRace: false,
                cancellationToken);
        }
    }

    private async Task<OidcBackChannelLogoutResult> RevokeProviderSessionAsync(
        string providerSessionHash,
        LogoutProviderSessionMapping? providerSession,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        if (providerSession is null)
        {
            // A valid unknown sid remains a successful non-enumerating no-op, but its tombstone
            // closes a concurrent or later replayed ID-token login for the same provider session.
            db.OidcProviderSessions.Add(new OidcProviderSession
            {
                Id = Guid.NewGuid(),
                SessionKeyHash = providerSessionHash,
                CreatedAtUtc = nowUtc,
                RevokedAtUtc = nowUtc,
                ReissueBlockedAtUtc = nowUtc
            });
            return OidcBackChannelLogoutResult.NoLiveMapping;
        }

        if (providerSession.ReissueBlockedAtUtc is not null
            || providerSession.AccountSessionId is null
            || providerSession.UserAccountId is null
            || providerSession.TenantId is null)
        {
            return OidcBackChannelLogoutResult.NoLiveMapping;
        }

        // Claim the provider-session row before reading its current local family. This serializes
        // against a fresh OIDC login that may be replacing a locally revoked family with the same
        // sid: whichever operation wins, no replacement can survive a validated provider logout.
        var claimedProviderSessions = await db.OidcProviderSessions
            .Where(candidate => candidate.Id == providerSession.Id
                && candidate.SessionKeyHash == providerSessionHash
                && candidate.ReissueBlockedAtUtc == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(
                        candidate => candidate.RevokedAtUtc,
                        candidate => candidate.RevokedAtUtc ?? nowUtc)
                    .SetProperty(candidate => candidate.ReissueBlockedAtUtc, nowUtc),
                cancellationToken);
        if (claimedProviderSessions != 1)
        {
            return OidcBackChannelLogoutResult.NoLiveMapping;
        }

        var claimedMapping = await db.OidcProviderSessions
            .AsNoTracking()
            .Where(candidate => candidate.Id == providerSession.Id)
            .Select(candidate => new
            {
                candidate.AccountSessionId,
                candidate.UserAccountId,
                candidate.TenantId
            })
            .SingleAsync(cancellationToken);
        if (claimedMapping.AccountSessionId is null
            || claimedMapping.UserAccountId is null
            || claimedMapping.TenantId is null)
        {
            return OidcBackChannelLogoutResult.NoLiveMapping;
        }

        if (!await AccountSessionRevocation.RevokeFamilyAsync(
                db,
                claimedMapping.AccountSessionId.Value,
                claimedMapping.UserAccountId.Value,
                claimedMapping.TenantId.Value,
                nowUtc,
                cancellationToken))
        {
            return OidcBackChannelLogoutResult.NoLiveMapping;
        }

        await AddActivityAsync(
            claimedMapping.TenantId.Value,
            nowUtc,
            "One GameHub session was invalidated by the identity provider.",
            cancellationToken);
        return OidcBackChannelLogoutResult.SessionInvalidated;
    }

    private async Task<OidcBackChannelLogoutResult> RevokeSubjectSessionsAsync(
        LogoutIdentityMapping? mapping,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        if (mapping is null)
        {
            return OidcBackChannelLogoutResult.NoLiveMapping;
        }

        // Permanently close every known provider sid before revoking local families. A concurrent
        // OIDC callback can then either commit first and be included below, or observe the claimed
        // provider tombstone and fail without reopening the account.
        await db.OidcProviderSessions
            .Where(session => session.UserAccountId == mapping.UserAccountId
                && session.TenantId == mapping.TenantId
                && session.ReissueBlockedAtUtc == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(
                        session => session.RevokedAtUtc,
                        session => session.RevokedAtUtc ?? nowUtc)
                    .SetProperty(session => session.ReissueBlockedAtUtc, nowUtc),
                cancellationToken);

        var updatedAccounts = await db.UserAccounts
            .Where(account => account.Id == mapping.UserAccountId
                && account.TenantId == mapping.TenantId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(
                    account => account.SessionVersion,
                    account => account.SessionVersion + 1),
                cancellationToken);
        if (updatedAccounts != 1)
        {
            return OidcBackChannelLogoutResult.NoLiveMapping;
        }

        await AccountSessionRevocation.RevokeAllFamiliesAsync(
            db,
            mapping.UserAccountId,
            mapping.TenantId,
            nowUtc,
            cancellationToken);

        await AddActivityAsync(
            mapping.TenantId,
            nowUtc,
            "All GameHub sessions were invalidated by the identity provider.",
            cancellationToken);
        return OidcBackChannelLogoutResult.AccountInvalidated;
    }

    private async Task AddActivityAsync(
        Guid tenantId,
        DateTime timestamp,
        string message,
        CancellationToken cancellationToken)
    {
        using var tenantScope = tenantExecution.BeginScope(tenantId);
        db.ActivityLogs.Add(new ActivityLog
        {
            Timestamp = timestamp,
            EventType = "OidcBackChannelLogout",
            Message = message,
            Details = null
        });
        // Save while the trusted tenant execution scope is active. The surrounding DB
        // transaction still commits or rolls back this row together with revocation state.
        await db.SaveChangesAsync(cancellationToken);
    }

    private static string HashReplayIdentity(string issuer, string jwtId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{issuer}\n{jwtId}")));

    private sealed record LogoutIdentityMapping(
        Guid ExternalIdentityId,
        Guid UserAccountId,
        Guid TenantId);

    private sealed record LogoutProviderSessionMapping(
        Guid Id,
        Guid? ExternalIdentityId,
        Guid? AccountSessionId,
        Guid? UserAccountId,
        Guid? TenantId,
        DateTime? ReissueBlockedAtUtc);
}

/// <summary>Secret-free durable outcome categories for one accepted provider logout.</summary>
public enum OidcBackChannelLogoutResult
{
    SessionInvalidated,
    AccountInvalidated,
    // Compatibility name retained for callers/tests that described the original account-wide
    // implementation before session-specific outcomes existed.
    Invalidated = AccountInvalidated,
    AlreadyProcessed,
    NoLiveMapping,
    SubjectSessionMismatch
}
