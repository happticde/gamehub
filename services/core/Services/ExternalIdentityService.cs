using GameHub.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace GameHub.Core.Services;

/// <summary>
/// Links and resolves validated external identities. No method accepts a requested tenant,
/// account, role, or email as an authentication selector.
/// </summary>
public sealed class ExternalIdentityService
{
    private readonly CoreContext _db;
    private readonly TimeProvider _timeProvider;

    public ExternalIdentityService(CoreContext db, TimeProvider timeProvider)
    {
        _db = db;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Attaches an issuer/subject pair to the exact signed-in account and tenant. An identity
    /// already owned by another account or another subject from the same issuer is rejected.
    /// </summary>
    public async Task<ExternalIdentityLinkResult> LinkAsync(
        Guid accountId,
        Guid tenantId,
        ValidatedOidcIdentity identity,
        CancellationToken cancellationToken = default)
    {
        var account = await _db.UserAccounts
            .Include(candidate => candidate.Tenant)
            .SingleOrDefaultAsync(
                candidate => candidate.Id == accountId && candidate.TenantId == tenantId,
                cancellationToken);
        if (account?.Tenant is null || account.Tenant.DeletionRequestedAtUtc is not null)
        {
            return ExternalIdentityLinkResult.AccountUnavailable;
        }

        var existing = await _db.ExternalIdentities.SingleOrDefaultAsync(
            candidate => candidate.Issuer == identity.Issuer
                && candidate.Subject == identity.Subject,
            cancellationToken);
        if (existing is not null)
        {
            return existing.UserAccountId == accountId && existing.TenantId == tenantId
                ? ExternalIdentityLinkResult.Linked
                : ExternalIdentityLinkResult.IdentityAlreadyLinked;
        }

        if (await _db.ExternalIdentities.AnyAsync(
                candidate => candidate.UserAccountId == accountId
                    && candidate.Issuer == identity.Issuer,
                cancellationToken))
        {
            return ExternalIdentityLinkResult.ProviderAlreadyLinked;
        }

        _db.ExternalIdentities.Add(new ExternalIdentity
        {
            Id = Guid.NewGuid(),
            Issuer = identity.Issuer,
            Subject = identity.Subject,
            UserAccountId = account.Id,
            TenantId = account.TenantId,
            CreatedAtUtc = _timeProvider.GetUtcNow().UtcDateTime
        });

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return ExternalIdentityLinkResult.Linked;
        }
        catch (DbUpdateException)
        {
            // Unique indexes remain authoritative when concurrent callbacks race.
            return ExternalIdentityLinkResult.IdentityAlreadyLinked;
        }
    }

    /// <summary>
    /// Resolves an exact issuer/subject mapping. Email collisions and all unrecognized claims
    /// produce no fallback; pending-deletion and deleted accounts remain unavailable.
    /// </summary>
    public async Task<UserAccount?> AuthenticateAsync(
        ValidatedOidcIdentity identity,
        CancellationToken cancellationToken = default)
    {
        var mapping = await _db.ExternalIdentities
            .Include(candidate => candidate.UserAccount)
                .ThenInclude(account => account!.Tenant)
            .SingleOrDefaultAsync(
                candidate => candidate.Issuer == identity.Issuer
                    && candidate.Subject == identity.Subject,
                cancellationToken);
        var account = mapping?.UserAccount;
        if (mapping is null
            || account?.Tenant is null
            || mapping.TenantId != account.TenantId
            || account.Tenant.DeletionRequestedAtUtc is not null)
        {
            return null;
        }

        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        mapping.LastLoginAtUtc = nowUtc;
        account.LastLoginAtUtc = nowUtc;
        await _db.SaveChangesAsync(cancellationToken);
        return account;
    }
}

/// <summary>Stable outcomes for explicit external-identity linking.</summary>
public enum ExternalIdentityLinkResult
{
    Linked,
    AccountUnavailable,
    IdentityAlreadyLinked,
    ProviderAlreadyLinked
}
