using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using GameHub.Core.Data;
using GameHub.Core.Mcp;
using GameHub.Shared.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace GameHub.Core.Services;

/// <summary>Immutable admission result. No browser or integration credential is included.</summary>
public sealed record McpGrantAdmissionResult(
    Guid GrantId, Guid AccountId, Guid TenantId, Guid FamilyId, Guid ProviderSessionId,
    string AuthorizationId, long SessionVersion, string Resource, string Scopes,
    DateTime ExpiresAtUtc)
{
    internal ClaimsPrincipal CreatePrincipal(string authenticationScheme, bool allowRenewal)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(authenticationScheme));
        principal.SetClaim(Claims.Subject, AccountId.ToString("D"));
        principal.SetClaim(GameHubClaimTypes.TenantId, TenantId.ToString("D"));
        principal.SetClaim(GameHubClaimTypes.AccountSessionId, FamilyId.ToString("D"));
        principal.SetClaim(GameHubClaimTypes.SessionVersion, SessionVersion.ToString(CultureInfo.InvariantCulture));
        var scopes = Scopes.Split(' ').ToList();
        if (allowRenewal) scopes.Add(OpenIddictConstants.Scopes.OfflineAccess);
        return principal.SetScopes(scopes).SetResources(Resource).SetAuthorizationId(AuthorizationId);
    }
}

/// <summary>
/// Orders consent and integration work against durable lifecycle changes. The caller must own
/// the transaction and commit protocol consumption or sensitive writes inside that same boundary.
/// This service registers no endpoint and does not validate or issue protocol credentials.
/// </summary>
public sealed class McpGrantAdmission(CoreContext db, McpRestResource resource, TimeProvider timeProvider)
{
    private McpRemoteResource? Remote { get; }

    /// <summary>Uses the independently configured native-client resource when managed setup is active.</summary>
    public McpGrantAdmission(CoreContext db, McpRestResource resource, TimeProvider timeProvider,
        IServiceProvider services) : this(db, resource, timeProvider) =>
        Remote = services.GetService<McpRemoteResource>();

    /// <summary>
    /// Creates consent from an explicitly authenticated normal browser ticket. Requested scopes
    /// must come from the library-validated pending request, never an approval form's scope list.
    /// Pending-code binding and one-time verification must complete before the caller commits.
    /// </summary>
    public Task<McpGrantAdmissionResult?> CreateAsync(
        AuthenticateResult browser, IReadOnlyCollection<string> requestedScopes, bool allowTrackingWrite,
        CancellationToken cancellationToken = default) =>
        CreateAsync(browser, requestedScopes, allowTrackingWrite, McpDeviceProtocol.ClientId, cancellationToken);

    internal async Task<McpGrantAdmissionResult?> CreateAsync(
        AuthenticateResult browser, IReadOnlyCollection<string> requestedScopes, bool allowTrackingWrite,
        string clientId, CancellationToken cancellationToken)
    {
        RequireTransaction();
        var expected = McpRemoteResource.ForClient(clientId, resource, Remote);
        if (expected is null || !requestedScopes.Contains(McpPermissions.LibraryRead)
            || requestedScopes.Count != requestedScopes.Distinct(StringComparer.Ordinal).Count()
            || requestedScopes.Any(scope => scope is not (McpPermissions.LibraryRead
                or McpPermissions.TrackingWrite or Scopes.OfflineAccess))
            || (allowTrackingWrite && !requestedScopes.Contains(McpPermissions.TrackingWrite)))
            return null;

        var admitted = await AdmitBrowserAsync(browser, cancellationToken);
        if (admitted is not { } owner) return null;
        var providerId = await db.OidcProviderSessions.AsNoTracking()
            .Where(row => row.AccountSessionId == owner.FamilyId && row.UserAccountId == owner.AccountId
                && row.TenantId == owner.TenantId)
            .Select(row => row.Id).SingleAsync(cancellationToken);

        var application = await db.Set<McpProtocolApplication>().AsNoTracking()
            .SingleOrDefaultAsync(row => row.ClientId == clientId, cancellationToken);
        if (application is not { ClientType: ClientTypes.Public, ConsentType: ConsentTypes.Explicit }) return null;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var familyId = Guid.NewGuid();
        var authorizationId = Guid.NewGuid().ToString("D");
        var scopes = allowTrackingWrite ? McpPermissions.ReadAndWrite : McpPermissions.LibraryRead;
        var protocolScopes = scopes.Split(' ').ToList();
        if (requestedScopes.Contains(Scopes.OfflineAccess)) protocolScopes.Add(Scopes.OfflineAccess);
        db.AccountSessions.Add(new AccountSession
        {
            Id = familyId, UserAccountId = owner.AccountId, TenantId = owner.TenantId, CreatedAtUtc = now
        });
        // Only ownership/consent storage is created here. OpenIddict remains the sole owner of
        // code verification, credential protection, consumption and rotation in the caller.
        db.Set<McpProtocolAuthorization>().Add(new McpProtocolAuthorization
        {
            Id = authorizationId, UserAccountId = owner.AccountId, TenantId = owner.TenantId,
            AccountSessionId = familyId, Application = await db.Set<McpProtocolApplication>()
                .SingleAsync(row => row.Id == application.Id, cancellationToken),
            Subject = owner.AccountId.ToString("D"), Status = Statuses.Valid,
            Type = AuthorizationTypes.Permanent, CreationDate = now,
            Scopes = JsonSerializer.Serialize(protocolScopes)
        });
        var grant = new McpGrant
        {
            Id = Guid.NewGuid(), UserAccountId = owner.AccountId, TenantId = owner.TenantId,
            AccountSessionId = familyId, ProtocolAuthorizationId = authorizationId,
            OidcProviderSessionId = providerId, ClientId = clientId,
            Resource = expected, Scopes = scopes,
            ApprovedSessionVersion = owner.Epoch, CreatedAtUtc = now, ExpiresAtUtc = now.AddDays(30)
        };
        db.McpGrants.Add(grant);
        await db.SaveChangesAsync(cancellationToken);
        return Snapshot(grant);
    }

    /// <summary>Admits approval or denial without creating any connection. Retain the caller's transaction.</summary>
    public async Task<bool> CanConsentAsync(AuthenticateResult browser, CancellationToken cancellationToken = default) =>
        await AdmitBrowserAsync(browser, cancellationToken) is not null;

    private async Task<Owner?> AdmitBrowserAsync(AuthenticateResult browser, CancellationToken cancellationToken)
    {
        RequireTransaction();
        if (!browser.Succeeded || browser.Ticket?.AuthenticationScheme != JwtBearerDefaults.AuthenticationScheme
            || browser.Principal is not { } principal
            || principal.Identities.ToArray() is not [ { IsAuthenticated: true } ]
            || principal.HasClaim(claim => claim.Type == GameHubClaimTypes.SessionPurpose)
            || !TryOwner(principal, ClaimTypes.NameIdentifier, out var owner)
            || One(principal, Claims.Subject) != owner.AccountId.ToString("D")
            || !await LockOwnerAsync(owner, cancellationToken)) return null;
        // Provider provenance is derived solely from the signed browser family, never supplied
        // by a client. An external link on a password-created family cannot satisfy this query.
        var providerId = await db.OidcProviderSessions.AsNoTracking()
            .Where(row => row.AccountSessionId == owner.FamilyId && row.UserAccountId == owner.AccountId
                && row.TenantId == owner.TenantId)
            .Select(row => (Guid?)row.Id).SingleOrDefaultAsync(cancellationToken);
        if (providerId is null
            || !await LockProviderAsync(owner, providerId.Value, browserConsent: true, cancellationToken)
            || !await LockFamilyAsync(owner, cancellationToken)) return null;

        return owner;
    }

    /// <summary>
    /// Admits an already protocol-validated device, refresh or access principal under locks.
    /// Token type is selected by the server operation; scopes/resources cannot exceed consent.
    /// Does not replace OpenIddict validation, refresh-reuse policy or expiry clamping on issuance.
    /// </summary>
    public async Task<McpGrantAdmissionResult?> AdmitAsync(
        ClaimsPrincipal principal, string tokenType, string? requiredPermission = null,
        CancellationToken cancellationToken = default) =>
        await AdmitTokenAsync(principal, tokenType, Statuses.Valid, requiredPermission, cancellationToken);

    /// <summary>
    /// Ends exactly the connection identified by a cryptographically validated, already consumed
    /// refresh token. The caller must commit this revocation despite the rejected token response.
    /// </summary>
    public async Task<bool> RevokeReusedRefreshAsync(ClaimsPrincipal principal,
        CancellationToken cancellationToken = default)
    {
        var grant = await AdmitTokenAsync(principal, TokenTypeIdentifiers.RefreshToken,
            Statuses.Redeemed, null, cancellationToken);
        if (grant is null) return false;
        await AccountSessionRevocation.RevokeFamilyAsync(db, grant.FamilyId, grant.AccountId,
            grant.TenantId, timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
        return true;
    }

    private async Task<McpGrantAdmissionResult?> AdmitTokenAsync(
        ClaimsPrincipal principal, string tokenType, string tokenStatus, string? requiredPermission,
        CancellationToken cancellationToken)
    {
        RequireTransaction();
        // Access validation exposes the token's audience; device/refresh principals carry the
        // authorization request's resource metadata. Both must identify exactly this resource.
        var resources = tokenType == TokenTypeIdentifiers.AccessToken
            ? principal.GetAudiences() : principal.GetResources();
        if (tokenType is not (TokenTypeIdentifiers.Private.DeviceCode or TokenTypeIdentifiers.Private.AuthorizationCode
                or TokenTypeIdentifiers.RefreshToken or TokenTypeIdentifiers.AccessToken)
            || principal.GetPresenters().ToArray() is not [var clientId]
            || McpRemoteResource.ForClient(clientId, resource, Remote) is not { } expected
            || (tokenType == TokenTypeIdentifiers.Private.DeviceCode && clientId != McpDeviceProtocol.ClientId)
            || (tokenType == TokenTypeIdentifiers.Private.AuthorizationCode && clientId == McpDeviceProtocol.ClientId)
            || !TryOwner(principal, Claims.Subject, out var owner)
            || principal.GetAuthorizationId() is not { Length: > 0 } authorizationId
            || principal.GetTokenId() is not { Length: > 0 } tokenId
            || !resources.SequenceEqual([expected])) return null;

        var grant = await db.McpGrants.AsNoTracking().SingleOrDefaultAsync(row =>
            row.UserAccountId == owner.AccountId && row.TenantId == owner.TenantId
            && row.AccountSessionId == owner.FamilyId && row.ProtocolAuthorizationId == authorizationId,
            cancellationToken);
        if (grant is null || grant.ApprovedSessionVersion != owner.Epoch
            || grant.Resource != expected || grant.ClientId != clientId
            || grant.Scopes is not (McpPermissions.LibraryRead or McpPermissions.ReadAndWrite)) return null;
        var scopes = principal.GetScopes();
        if (!scopes.Contains(McpPermissions.LibraryRead)
            || scopes.Any(scope => scope != Scopes.OfflineAccess && !grant.Scopes.Split(' ').Contains(scope))
            || (requiredPermission is not null && !scopes.Contains(requiredPermission))) return null;

        if (!await LockOwnerAsync(owner, cancellationToken)
            || !await LockProviderAsync(owner, grant.OidcProviderSessionId, browserConsent: false, cancellationToken)
            || !await LockFamilyAsync(owner, cancellationToken)) return null;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (await db.McpGrants.Where(row => row.Id == grant.Id && row.RevokedAtUtc == null
                && row.CreatedAtUtc <= now && row.ExpiresAtUtc > now)
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.RevokedAtUtc, row => row.RevokedAtUtc),
                cancellationToken) != 1) return null;
        var subject = owner.AccountId.ToString("D");
        if (await db.Set<McpProtocolAuthorization>().Where(row => row.Id == authorizationId
                && row.UserAccountId == owner.AccountId && row.TenantId == owner.TenantId
                && row.AccountSessionId == owner.FamilyId && row.Status == Statuses.Valid && row.Subject == subject
                && row.Application != null && row.Application.ClientId == grant.ClientId)
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.Status, row => row.Status),
                cancellationToken) != 1) return null;
        if (await db.Set<McpProtocolToken>().Where(row => row.Id == tokenId && row.Status == tokenStatus
                && row.Type == tokenType && row.Subject == subject && row.ExpirationDate > now
                && row.Authorization != null && row.Authorization.Id == authorizationId
                && row.Application != null && row.Application.ClientId == grant.ClientId)
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.Status, row => row.Status),
                cancellationToken) != 1) return null;
        // A lock wait must not turn a formerly live grant into an admission after absolute expiry.
        if (grant.ExpiresAtUtc <= timeProvider.GetUtcNow().UtcDateTime) return null;
        return Snapshot(grant);
    }

    private void RequireTransaction()
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("MCP admission requires the caller's database transaction.");
        foreach (var uri in new[] { resource.Issuer, resource.Resource })
            if (!uri.IsAbsoluteUri || uri.Scheme != "https" || uri.UserInfo.Length != 0
                || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                throw new InvalidOperationException("MCP admission requires validated exact HTTPS trust.");
    }

    private async Task<bool> LockOwnerAsync(Owner owner, CancellationToken cancellationToken)
    {
        // No-op writes acquire database row locks without changing the epoch or consent. Unlike
        // a process mutex or a prior read, these order against existing lifecycle transactions
        // across replicas. Contention/serialization failures must abort, never retry token work.
        if (await db.Tenants.Where(row => row.Id == owner.TenantId && row.DeletionRequestedAtUtc == null)
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.DeletionRequestedAtUtc,
                row => row.DeletionRequestedAtUtc), cancellationToken) != 1) return false;
        return await db.UserAccounts.Where(row => row.Id == owner.AccountId && row.TenantId == owner.TenantId
                && row.SessionVersion == owner.Epoch)
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.SessionVersion, row => row.SessionVersion),
                cancellationToken) == 1;
    }

    private Task<int> ClaimProviderAsync(Owner owner, Guid providerId, bool browserConsent,
        CancellationToken cancellationToken) => db.OidcProviderSessions.Where(row => row.Id == providerId
            && row.UserAccountId == owner.AccountId && row.TenantId == owner.TenantId
            && row.ReissueBlockedAtUtc == null && row.AccountSessionId != null
            && (browserConsent || row.AccountSessionId != owner.FamilyId)
            && row.ExternalIdentity != null && row.ExternalIdentity.UserAccountId == owner.AccountId
            && row.ExternalIdentity.TenantId == owner.TenantId
            && (!browserConsent || (row.AccountSessionId == owner.FamilyId && row.RevokedAtUtc == null)))
        .ExecuteUpdateAsync(update => update.SetProperty(row => row.ReissueBlockedAtUtc,
            row => row.ReissueBlockedAtUtc), cancellationToken);

    private async Task<bool> LockProviderAsync(Owner owner, Guid providerId, bool browserConsent,
        CancellationToken cancellationToken) =>
        await ClaimProviderAsync(owner, providerId, browserConsent, cancellationToken) == 1;

    private async Task<bool> LockFamilyAsync(Owner owner, CancellationToken cancellationToken) =>
        await db.AccountSessions.Where(row => row.Id == owner.FamilyId && row.UserAccountId == owner.AccountId
                && row.TenantId == owner.TenantId && row.RevokedAtUtc == null)
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.RevokedAtUtc, row => row.RevokedAtUtc),
                cancellationToken) == 1;

    private static bool TryOwner(ClaimsPrincipal principal, string subjectType, out Owner owner)
    {
        owner = default;
        if (!Guid.TryParse(One(principal, subjectType), out var accountId) || accountId == Guid.Empty
            || !Guid.TryParse(One(principal, GameHubClaimTypes.TenantId), out var tenantId) || tenantId == Guid.Empty
            || !Guid.TryParse(One(principal, GameHubClaimTypes.AccountSessionId), out var familyId) || familyId == Guid.Empty
            || !long.TryParse(One(principal, GameHubClaimTypes.SessionVersion), NumberStyles.None,
                CultureInfo.InvariantCulture, out var epoch)) return false;
        owner = new(accountId, tenantId, familyId, epoch);
        return true;
    }

    private static string? One(ClaimsPrincipal principal, string type) =>
        principal.FindAll(type).Select(claim => claim.Value).ToArray() is [var value] ? value : null;

    private static McpGrantAdmissionResult Snapshot(McpGrant grant) => new(grant.Id, grant.UserAccountId,
        grant.TenantId, grant.AccountSessionId, grant.OidcProviderSessionId, grant.ProtocolAuthorizationId,
        grant.ApprovedSessionVersion, grant.Resource, grant.Scopes, grant.ExpiresAtUtc);

    private readonly record struct Owner(Guid AccountId, Guid TenantId, Guid FamilyId, long Epoch);
}
