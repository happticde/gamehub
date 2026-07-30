using System.Security.Cryptography;
using System.Text;
using GameHub.Core.Data;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace GameHub.Core.Services;

/// <summary>
/// Issues, rotates, and revokes opaque refresh tokens within durable local session families.
/// Every normal access JWT and every rotating refresh row references the same
/// <see cref="AccountSession"/>, so either credential path can be invalidated immediately without
/// treating a rotating refresh-row ID as a stable session identity.
/// </summary>
public sealed class RefreshTokenService
{
    private const int SecretByteCount = 32;
    private const int MaximumSubmittedTokenLength = 256;

    private readonly CoreContext _db;
    private readonly JwtSettings _settings;
    private readonly TimeProvider _timeProvider;

    public RefreshTokenService(
        CoreContext db,
        JwtSettings settings,
        TimeProvider timeProvider)
    {
        _db = db;
        _settings = settings;
        _timeProvider = timeProvider;
    }

    /// <summary>Creates and persists one new non-OIDC local session family.</summary>
    public async Task<IssuedRefreshToken> IssueAsync(
        UserAccount account,
        CancellationToken cancellationToken = default)
    {
        var token = StageIssue(account);
        await _db.SaveChangesAsync(cancellationToken);
        return token;
    }

    /// <summary>
    /// Stages one new non-OIDC local session family. Transactional callers can commit it together
    /// with account and tenant lifecycle rows.
    /// </summary>
    public IssuedRefreshToken StageIssue(UserAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        var accountSession = CreateAccountSession(account);
        _db.AccountSessions.Add(accountSession);
        return StageRefreshToken(account, accountSession.Id);
    }

    /// <summary>
    /// Creates or reuses the one local family mapped to a fully validated OIDC issuer/session
    /// pair. A durable logout tombstone or a mapping to another identity fails closed.
    /// </summary>
    public async Task<OidcRefreshTokenIssueResult> IssueOidcAsync(
        OidcAuthenticatedAccount authentication,
        string providerSessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authentication);
        if (string.IsNullOrWhiteSpace(providerSessionId) || providerSessionId.Length > 512)
        {
            return OidcRefreshTokenIssueResult.Denied;
        }

        var sessionKeyHash = OidcProviderSessionKey.Hash(
            authentication.ExternalIdentity.Issuer,
            providerSessionId);
        return await IssueOidcCoreAsync(
            authentication,
            sessionKeyHash,
            retryAfterUniqueRace: true,
            cancellationToken);
    }

    /// <summary>
    /// Stages a new OIDC-mapped local family for JIT provisioning. The caller's transaction and
    /// the unique issuer-bound hash are the concurrency authority.
    /// </summary>
    public IssuedRefreshToken StageOidcIssue(
        UserAccount account,
        ExternalIdentity externalIdentity,
        string providerSessionId)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(externalIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerSessionId);

        var accountSession = CreateAccountSession(account);
        _db.AccountSessions.Add(accountSession);
        _db.OidcProviderSessions.Add(new OidcProviderSession
        {
            Id = Guid.NewGuid(),
            SessionKeyHash = OidcProviderSessionKey.Hash(
                externalIdentity.Issuer,
                providerSessionId),
            ExternalIdentityId = externalIdentity.Id,
            AccountSessionId = accountSession.Id,
            UserAccountId = account.Id,
            TenantId = account.TenantId,
            CreatedAtUtc = accountSession.CreatedAtUtc
        });
        return StageRefreshToken(account, accountSession.Id);
    }

    /// <summary>
    /// Exchanges an active refresh token exactly once while retaining the same durable local
    /// session family. Normal rotation therefore does not invalidate access JWTs already issued
    /// for that family.
    /// </summary>
    public async Task<RefreshTokenRotationResult> RotateAsync(
        string? plaintextToken,
        CancellationToken cancellationToken = default)
    {
        if (!TryHashSubmittedToken(plaintextToken, out var tokenHash))
        {
            return RefreshTokenRotationResult.Invalid;
        }

        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var existing = await _db.RefreshTokenSessions
            .AsNoTracking()
            .Include(session => session.AccountSession)
            .Include(session => session.UserAccount)
                .ThenInclude(account => account!.Tenant)
            .SingleOrDefaultAsync(
                session => session.TokenHash == tokenHash,
                cancellationToken);
        if (existing?.UserAccount is null
            || existing.AccountSession is null
            || existing.AccountSessionId is null
            || existing.AccountSession.Id != existing.AccountSessionId
            || existing.AccountSession.UserAccountId != existing.UserAccountId
            || existing.AccountSession.TenantId != existing.UserAccount.TenantId
            || existing.AccountSession.RevokedAtUtc is not null
            || existing.UserAccount.Tenant?.DeletionRequestedAtUtc is not null
            || existing.SessionVersion != existing.UserAccount.SessionVersion
            || existing.ExpiresAtUtc <= nowUtc)
        {
            // Legacy rows without a stable family are deliberately unusable after the additive
            // migration. Unknown, expired, and upgrade-invalidated tokens remain indistinguishable.
            return RefreshTokenRotationResult.Invalid;
        }

        if (existing.ConsumedAtUtc is not null || existing.RevokedAtUtc is not null)
        {
            await using var replayTransaction =
                await _db.Database.BeginTransactionAsync(cancellationToken);
            await AccountSessionRevocation.RevokeAllFamiliesAsync(
                _db,
                existing.UserAccountId,
                existing.UserAccount.TenantId,
                nowUtc,
                cancellationToken);
            await replayTransaction.CommitAsync(cancellationToken);
            return RefreshTokenRotationResult.Invalid;
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        var consumedRows = await _db.RefreshTokenSessions
            .Where(session =>
                session.Id == existing.Id
                && session.AccountSessionId == existing.AccountSessionId
                && _db.AccountSessions.Any(accountSession =>
                    accountSession.Id == existing.AccountSessionId
                    && accountSession.UserAccountId == existing.UserAccountId
                    && accountSession.RevokedAtUtc == null)
                && session.ConsumedAtUtc == null
                && session.RevokedAtUtc == null
                && session.ExpiresAtUtc > nowUtc)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(session => session.ConsumedAtUtc, nowUtc),
                cancellationToken);
        if (consumedRows != 1)
        {
            // A targeted family revocation can win this race legitimately. In that case the
            // family is already closed and no account-wide replay response is needed. If the
            // family is still active, another refresh consumed the secret and the stronger
            // historical replay defense revokes every family.
            var familyStillActive = await IsAccountSessionActiveAsync(
                existing.AccountSessionId.Value,
                existing.UserAccountId,
                existing.UserAccount.TenantId,
                cancellationToken);
            if (familyStillActive)
            {
                await AccountSessionRevocation.RevokeAllFamiliesAsync(
                    _db,
                    existing.UserAccountId,
                    existing.UserAccount.TenantId,
                    nowUtc,
                    cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return RefreshTokenRotationResult.Invalid;
        }

        var replacement = CreateRefreshToken(
            existing.UserAccountId,
            existing.UserAccount.SessionVersion,
            existing.AccountSessionId.Value);
        _db.RefreshTokenSessions.Add(replacement.Session);
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        if (!await IsAccountSessionActiveAsync(
                existing.AccountSessionId.Value,
                existing.UserAccountId,
                existing.UserAccount.TenantId,
                cancellationToken))
        {
            return RefreshTokenRotationResult.Invalid;
        }

        return RefreshTokenRotationResult.Succeeded(
            existing.UserAccount,
            replacement.Token);
    }

    /// <summary>
    /// Revokes the durable family selected by one active opaque token. Access and every refresh
    /// token in that family stop together. A replayed consumed token keeps the stronger historical
    /// behavior and invalidates all local families for the account.
    /// </summary>
    public async Task RevokeAsync(
        string? plaintextToken,
        CancellationToken cancellationToken = default)
    {
        if (!TryHashSubmittedToken(plaintextToken, out var tokenHash))
        {
            return;
        }

        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var existing = await _db.RefreshTokenSessions
            .AsNoTracking()
            .Where(session => session.TokenHash == tokenHash)
            .Select(session => new
            {
                session.UserAccountId,
                session.AccountSessionId,
                session.ConsumedAtUtc,
                TenantId = session.UserAccount!.TenantId
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (existing is null || existing.AccountSessionId is null)
        {
            return;
        }

        if (existing.ConsumedAtUtc is not null)
        {
            await using var replayTransaction =
                await _db.Database.BeginTransactionAsync(cancellationToken);
            await AccountSessionRevocation.RevokeAllFamiliesAsync(
                _db,
                existing.UserAccountId,
                existing.TenantId,
                nowUtc,
                cancellationToken);
            await replayTransaction.CommitAsync(cancellationToken);
            return;
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        await AccountSessionRevocation.RevokeFamilyAsync(
            _db,
            existing.AccountSessionId.Value,
            existing.UserAccountId,
            existing.TenantId,
            nowUtc,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<OidcRefreshTokenIssueResult> IssueOidcCoreAsync(
        OidcAuthenticatedAccount authentication,
        string sessionKeyHash,
        bool retryAfterUniqueRace,
        CancellationToken cancellationToken)
    {
        var account = authentication.Account;
        var identity = authentication.ExternalIdentity;
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var providerSession = await _db.OidcProviderSessions
                .AsNoTracking()
                .Include(candidate => candidate.AccountSession)
                .SingleOrDefaultAsync(
                    candidate => candidate.SessionKeyHash == sessionKeyHash,
                    cancellationToken);

            Guid accountSessionId;
            if (providerSession is null)
            {
                var accountSession = CreateAccountSession(account);
                accountSessionId = accountSession.Id;
                _db.AccountSessions.Add(accountSession);
                _db.OidcProviderSessions.Add(new OidcProviderSession
                {
                    Id = Guid.NewGuid(),
                    SessionKeyHash = sessionKeyHash,
                    ExternalIdentityId = identity.Id,
                    AccountSessionId = accountSession.Id,
                    UserAccountId = account.Id,
                    TenantId = account.TenantId,
                    CreatedAtUtc = accountSession.CreatedAtUtc
                });
            }
            else if (providerSession.RevokedAtUtc is not null
                || providerSession.AccountSession is null
                || providerSession.AccountSessionId is null
                || providerSession.ExternalIdentityId != identity.Id
                || providerSession.UserAccountId != account.Id
                || providerSession.TenantId != account.TenantId
                || providerSession.AccountSession.UserAccountId != account.Id
                || providerSession.AccountSession.TenantId != account.TenantId
                || providerSession.AccountSession.RevokedAtUtc is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return OidcRefreshTokenIssueResult.Denied;
            }
            else
            {
                accountSessionId = providerSession.AccountSessionId.Value;
            }

            var refreshToken = StageRefreshToken(account, accountSessionId);
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            if (!await IsAccountSessionActiveAsync(
                    accountSessionId,
                    account.Id,
                    account.TenantId,
                    cancellationToken))
            {
                return OidcRefreshTokenIssueResult.Denied;
            }

            return OidcRefreshTokenIssueResult.Succeeded(refreshToken);
        }
        catch (DbUpdateException) when (retryAfterUniqueRace)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            _db.ChangeTracker.Clear();
            return await IssueOidcCoreAsync(
                authentication,
                sessionKeyHash,
                retryAfterUniqueRace: false,
                cancellationToken);
        }
    }

    private AccountSession CreateAccountSession(UserAccount account) => new()
    {
        Id = Guid.NewGuid(),
        UserAccountId = account.Id,
        TenantId = account.TenantId,
        CreatedAtUtc = _timeProvider.GetUtcNow().UtcDateTime
    };

    private IssuedRefreshToken StageRefreshToken(
        UserAccount account,
        Guid accountSessionId)
    {
        var issued = CreateRefreshToken(
            account.Id,
            account.SessionVersion,
            accountSessionId);
        _db.RefreshTokenSessions.Add(issued.Session);
        return issued.Token;
    }

    private (RefreshTokenSession Session, IssuedRefreshToken Token) CreateRefreshToken(
        Guid accountId,
        long sessionVersion,
        Guid accountSessionId)
    {
        var plaintext = WebEncoders.Base64UrlEncode(
            RandomNumberGenerator.GetBytes(SecretByteCount));
        var createdAt = _timeProvider.GetUtcNow();
        var expiresAt = createdAt.AddDays(_settings.RefreshTokenLifetimeDays);
        var session = new RefreshTokenSession
        {
            Id = Guid.NewGuid(),
            UserAccountId = accountId,
            AccountSessionId = accountSessionId,
            SessionVersion = sessionVersion,
            TokenHash = HashToken(plaintext),
            CreatedAtUtc = createdAt.UtcDateTime,
            ExpiresAtUtc = expiresAt.UtcDateTime
        };
        return (
            session,
            new IssuedRefreshToken(plaintext, expiresAt, accountSessionId));
    }

    private Task<bool> IsAccountSessionActiveAsync(
        Guid accountSessionId,
        Guid accountId,
        Guid tenantId,
        CancellationToken cancellationToken) =>
        _db.AccountSessions.AsNoTracking().AnyAsync(
            session => session.Id == accountSessionId
                && session.UserAccountId == accountId
                && session.TenantId == tenantId
                && session.RevokedAtUtc == null,
            cancellationToken);

    private static bool TryHashSubmittedToken(
        string? plaintextToken,
        out string tokenHash)
    {
        tokenHash = "";
        if (string.IsNullOrWhiteSpace(plaintextToken)
            || plaintextToken.Length > MaximumSubmittedTokenLength)
        {
            return false;
        }

        tokenHash = HashToken(plaintextToken);
        return true;
    }

    private static string HashToken(string plaintextToken) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plaintextToken)));
}

/// <summary>Plaintext refresh secret returned once together with its stable local family.</summary>
public sealed record IssuedRefreshToken(
    string RefreshToken,
    DateTimeOffset ExpiresAtUtc,
    Guid AccountSessionId);

/// <summary>Result of establishing or reusing a validated OIDC session family.</summary>
public sealed record OidcRefreshTokenIssueResult(IssuedRefreshToken? RefreshToken)
{
    public bool Issued => RefreshToken is not null;

    public static OidcRefreshTokenIssueResult Denied { get; } =
        new((IssuedRefreshToken?)null);

    public static OidcRefreshTokenIssueResult Succeeded(IssuedRefreshToken refreshToken) =>
        new(refreshToken);
}

/// <summary>Internal result containing verified account and stable family after rotation.</summary>
public sealed record RefreshTokenRotationResult(
    UserAccount? Account,
    IssuedRefreshToken? RefreshToken)
{
    public bool Rotated => Account is not null && RefreshToken is not null;

    public static RefreshTokenRotationResult Invalid { get; } = new(null, null);

    public static RefreshTokenRotationResult Succeeded(
        UserAccount account,
        IssuedRefreshToken refreshToken) =>
        new(account, refreshToken);
}
