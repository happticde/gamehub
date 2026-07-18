using System.Security.Cryptography;
using System.Text;
using GameHub.Core.Data;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace GameHub.Core.Services;

/// <summary>
/// Issues, rotates, and revokes opaque refresh tokens. Access JWTs keep their signed authorization
/// claims but Core also checks the live account/session epoch; refresh tokens additionally use a
/// durable per-secret row so a session can be consumed, rotated, or revoked immediately.
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

    /// <summary>
    /// Creates a new refresh session after password authentication. Only the hash is persisted;
    /// the plaintext secret exists just long enough to be returned once to the client.
    /// </summary>
    public async Task<IssuedRefreshToken> IssueAsync(
        UserAccount account,
        CancellationToken cancellationToken = default)
    {
        var token = StageIssue(account);
        await _db.SaveChangesAsync(cancellationToken);
        return token;
    }

    /// <summary>
    /// Stages a new refresh session without saving it. Transactional workflows use this method so
    /// account, mapping, activity, and session rows can commit in one atomic SaveChanges call.
    /// </summary>
    public IssuedRefreshToken StageIssue(UserAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);

        var issued = CreateSession(account.Id, account.SessionVersion);
        _db.RefreshTokenSessions.Add(issued.Session);
        return issued.Token;
    }

    /// <summary>
    /// Exchanges an active refresh token exactly once. The conditional database update is the
    /// concurrency gate: if two requests race with the same secret, only one can mark it used.
    /// Reusing an already consumed secret is treated as possible theft and revokes every still-
    /// active session for that account.
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
            .Include(session => session.UserAccount)
                .ThenInclude(account => account!.Tenant)
            .SingleOrDefaultAsync(
                session => session.TokenHash == tokenHash,
                cancellationToken);
        if (existing?.UserAccount is null
            || existing.UserAccount.Tenant?.DeletionRequestedAtUtc is not null
            || existing.SessionVersion != existing.UserAccount.SessionVersion
            || existing.ExpiresAtUtc <= nowUtc)
        {
            // Unknown and naturally expired tokens get the same result. This avoids turning
            // the endpoint into an oracle that reveals which session secrets once existed.
            return RefreshTokenRotationResult.Invalid;
        }

        if (existing.ConsumedAtUtc is not null || existing.RevokedAtUtc is not null)
        {
            await RevokeActiveSessionsAsync(existing.UserAccountId, nowUtc, cancellationToken);
            return RefreshTokenRotationResult.Invalid;
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        // ExecuteUpdate produces one SQL UPDATE with all validity conditions in its WHERE
        // clause. This is stronger than changing the previously read entity: another request
        // may have consumed it between SELECT and UPDATE.
        var consumedRows = await _db.RefreshTokenSessions
            .Where(session =>
                session.Id == existing.Id
                && session.ConsumedAtUtc == null
                && session.RevokedAtUtc == null
                && session.ExpiresAtUtc > nowUtc)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(session => session.ConsumedAtUtc, nowUtc),
                cancellationToken);
        if (consumedRows != 1)
        {
            await RevokeActiveSessionsAsync(existing.UserAccountId, nowUtc, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return RefreshTokenRotationResult.Invalid;
        }

        var replacement = CreateSession(
            existing.UserAccountId,
            existing.UserAccount.SessionVersion);
        _db.RefreshTokenSessions.Add(replacement.Session);
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return RefreshTokenRotationResult.Succeeded(existing.UserAccount, replacement.Token);
    }

    /// <summary>
    /// Revokes a refresh session for logout. The operation intentionally does not reveal
    /// whether the submitted secret existed, so callers always receive the same HTTP result.
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
                session.Id,
                session.UserAccountId,
                session.ConsumedAtUtc
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (existing is null)
        {
            return;
        }

        if (existing.ConsumedAtUtc is not null)
        {
            // A consumed token should no longer be available to a legitimate client. Seeing
            // it again is a replay signal, so invalidate any replacement/session still active.
            await RevokeActiveSessionsAsync(existing.UserAccountId, nowUtc, cancellationToken);
            return;
        }

        await _db.RefreshTokenSessions
            .Where(session => session.Id == existing.Id && session.RevokedAtUtc == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(session => session.RevokedAtUtc, nowUtc),
                cancellationToken);
    }

    private (RefreshTokenSession Session, IssuedRefreshToken Token) CreateSession(
        Guid accountId,
        long sessionVersion)
    {
        // RandomNumberGenerator uses the operating system's cryptographic random source.
        // Base64Url is safe in JSON and HTTP without '+' '/' or padding characters.
        var plaintext = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(SecretByteCount));
        var createdAt = _timeProvider.GetUtcNow();
        var expiresAt = createdAt.AddDays(_settings.RefreshTokenLifetimeDays);
        var session = new RefreshTokenSession
        {
            Id = Guid.NewGuid(),
            UserAccountId = accountId,
            SessionVersion = sessionVersion,
            TokenHash = HashToken(plaintext),
            CreatedAtUtc = createdAt.UtcDateTime,
            ExpiresAtUtc = expiresAt.UtcDateTime
        };
        return (session, new IssuedRefreshToken(plaintext, expiresAt));
    }

    private async Task RevokeActiveSessionsAsync(
        Guid accountId,
        DateTime revokedAtUtc,
        CancellationToken cancellationToken)
    {
        await _db.RefreshTokenSessions
            .Where(session =>
                session.UserAccountId == accountId
                && session.ConsumedAtUtc == null
                && session.RevokedAtUtc == null
                && session.ExpiresAtUtc > revokedAtUtc)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(session => session.RevokedAtUtc, revokedAtUtc),
                cancellationToken);
    }

    private static bool TryHashSubmittedToken(string? plaintextToken, out string tokenHash)
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

/// <summary>Plaintext refresh secret returned once together with its absolute expiry.</summary>
public sealed record IssuedRefreshToken(string RefreshToken, DateTimeOffset ExpiresAtUtc);

/// <summary>Internal result containing a verified account only after successful rotation.</summary>
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
