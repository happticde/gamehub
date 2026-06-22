using GameHub.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace GameHub.Core.Services;

/// <summary>
/// Links and resolves validated external identities. No method accepts a requested tenant,
/// account, role, or email as an authentication selector.
///
/// This service begins after <see cref="OidcTokenValidator"/> has authenticated the provider
/// proof. It owns the second half of authentication: translating the stable external identity
/// into a local account/tenant authorization boundary without allowing claim-based shortcuts.
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
    /// The account and tenant arguments originate from a validated GameHub JWT in the controller;
    /// the external token contributes neither value.
    /// </summary>
    public async Task<ExternalIdentityLinkResult> LinkAsync(
        Guid accountId,
        Guid tenantId,
        ValidatedOidcIdentity identity,
        CancellationToken cancellationToken = default)
    {
        // First prove that the signed GameHub pair still exists and is not being deleted. This
        // closes the race where a valid access JWT outlives an account lifecycle transition.
        var account = await _db.UserAccounts
            .Include(candidate => candidate.Tenant)
            .SingleOrDefaultAsync(
                candidate => candidate.Id == accountId && candidate.TenantId == tenantId,
                cancellationToken);
        if (account?.Tenant is null || account.Tenant.DeletionRequestedAtUtc is not null)
        {
            return ExternalIdentityLinkResult.AccountUnavailable;
        }

        // `(Issuer, Subject)` is the only external lookup key. There is intentionally no query by
        // email because provider email reuse/collisions must never create account takeover.
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

        // Replacing a subject at the same provider could be a recovery/takeover operation. Refuse
        // implicit replacement until a dedicated reauthentication policy exists.
        if (await _db.ExternalIdentities.AnyAsync(
                candidate => candidate.UserAccountId == accountId
                    && candidate.Issuer == identity.Issuer,
                cancellationToken))
        {
            return ExternalIdentityLinkResult.ProviderAlreadyLinked;
        }

        // TenantId is copied from the authenticated database account, never from OIDC claims or
        // request JSON. The composite database FK enforces the same invariant below this code.
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
            // Unique indexes remain authoritative when concurrent callbacks race between the
            // application-level checks and SaveChanges.
            return ExternalIdentityLinkResult.IdentityAlreadyLinked;
        }
    }

    /// <summary>
    /// Resolves an exact issuer/subject mapping. Email collisions and all unrecognized claims
    /// produce no fallback; pending-deletion and deleted accounts remain unavailable. Returning
    /// <see langword="null"/> for every unavailable case supports a non-enumerating API response.
    /// </summary>
    public async Task<UserAccount?> AuthenticateAsync(
        ValidatedOidcIdentity identity,
        CancellationToken cancellationToken = default)
    {
        // Login begins without a trusted tenant, so this is a global identity lookup. Trust is
        // established only after the exact mapping and its related account/tenant are loaded.
        var mapping = await _db.ExternalIdentities
            .Include(candidate => candidate.UserAccount)
                .ThenInclude(account => account!.Tenant)
            .SingleOrDefaultAsync(
                candidate => candidate.Issuer == identity.Issuer
                    && candidate.Subject == identity.Subject,
                cancellationToken);
        var account = mapping?.UserAccount;
        // The FK should already guarantee matching tenant IDs. Rechecking makes the authorization
        // invariant explicit and fails closed if legacy/corrupt data ever bypasses the constraint.
        if (mapping is null
            || account?.Tenant is null
            || mapping.TenantId != account.TenantId
            || account.Tenant.DeletionRequestedAtUtc is not null)
        {
            return null;
        }

        // Login timestamps are audit/display state only; they do not extend refresh lifetime or
        // affect the stable mapping.
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        mapping.LastLoginAtUtc = nowUtc;
        account.LastLoginAtUtc = nowUtc;
        await _db.SaveChangesAsync(cancellationToken);
        return account;
    }

    /// <summary>
    /// Reports whether the account already owns a mapping for the configured issuer. This query
    /// supports the local bootstrap boundary only; it never searches by email or accepts a tenant
    /// selected by a request.
    /// </summary>
    public Task<bool> HasIssuerLinkAsync(
        UserAccount account,
        string issuer,
        CancellationToken cancellationToken = default) =>
        _db.ExternalIdentities.AsNoTracking().AnyAsync(
            identity => identity.UserAccountId == account.Id
                && identity.TenantId == account.TenantId
                && identity.Issuer == issuer,
            cancellationToken);
}

/// <summary>Stable outcomes for explicit external-identity linking.</summary>
public enum ExternalIdentityLinkResult
{
    /// <summary>The mapping was created or was already linked to this exact account/tenant.</summary>
    Linked,
    /// <summary>The signed account/tenant no longer exists or is pending deletion.</summary>
    AccountUnavailable,
    /// <summary>The issuer/subject pair belongs to another GameHub account.</summary>
    IdentityAlreadyLinked,
    /// <summary>This account already has a different subject linked for the same issuer.</summary>
    ProviderAlreadyLinked
}
