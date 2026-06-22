using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using GameHub.Core.Data;
using GameHub.Core.Models.Auth;
using GameHub.Core.Services;
using GameHub.Shared.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace GameHub.Core.Controllers;

/// <summary>
/// Provisions local credentials and manages JWT access/refresh sessions.
///
/// The endpoints have deliberately different trust models:
///   - bootstrap trusts the pre-existing server API key to choose a tenant;
///   - token starts anonymous, verifies email/password, then signs the account's tenant ID.
///   - refresh trusts a one-time opaque secret whose hash selects the database account;
///   - revoke invalidates that opaque session without exposing whether it existed.
/// At no point does a request body get to submit or override a tenant identifier.
/// </summary>
[ApiController]
[Route("api/auth")]
public sealed class AuthController : ControllerBase
{
    private readonly AccountService _accounts;
    private readonly JwtTokenService _tokens;
    private readonly RefreshTokenService _refreshTokens;
    private readonly OidcTokenValidator _oidcTokens;
    private readonly ExternalIdentityService _externalIdentities;
    private readonly OidcSettings _oidcSettings;
    private readonly MultiUserEnablementSettings _multiUser;
    private readonly AccountDeletionService _deletion;
    private readonly ITenantContext _tenantContext;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AuthController> _logger;

    /// <summary>Initializes the authentication API with scoped account and tenant services.</summary>
    public AuthController(
        AccountService accounts,
        JwtTokenService tokens,
        RefreshTokenService refreshTokens,
        OidcTokenValidator oidcTokens,
        ExternalIdentityService externalIdentities,
        OidcSettings oidcSettings,
        MultiUserEnablementSettings multiUser,
        AccountDeletionService deletion,
        ITenantContext tenantContext,
        IConfiguration configuration,
        ILogger<AuthController> logger)
    {
        _accounts = accounts;
        _tokens = tokens;
        _refreshTokens = refreshTokens;
        _oidcTokens = oidcTokens;
        _externalIdentities = externalIdentities;
        _oidcSettings = oidcSettings;
        _multiUser = multiUser;
        _deletion = deletion;
        _tenantContext = tenantContext;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// POST: api/auth/bootstrap
    /// Creates the first local account for the API key's configured tenant. This endpoint is
    /// the bridge from legacy API-key trust to password-based JWT authentication.
    /// </summary>
    [HttpPost("bootstrap")]
    // Explicitly name ApiKey here instead of using the composite default. Otherwise an
    // already-issued JWT could bootstrap or replace account credentials.
    [Authorize(AuthenticationSchemes = ApiKeyAuthenticationHandler.SchemeName)]
    [ProducesResponseType<AccountResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<AuthErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<AuthErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<AuthErrorResponse>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Bootstrap(
        [FromBody] BootstrapAccountRequest request,
        CancellationToken cancellationToken)
    {
        // ApiKeyAuthenticationHandler intentionally treats an empty key as authenticated for
        // backward-compatible local mode. Bootstrap is more sensitive: without a configured
        // key, anybody reaching the server could claim the first account. Refuse that state.
        if (string.IsNullOrWhiteSpace(_configuration["Authentication:ApiKey"]))
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new AuthErrorResponse(
                    "api_key_required",
                    "Configure Authentication:ApiKey before bootstrapping an account."));
        }
        if (_multiUser.Mode.IsEnabled)
        {
            return Conflict(new AuthErrorResponse(
                "multi_user_provisioning_requires_disabled_mode",
                "Disable guarded multi-user mode before bootstrapping an account."));
        }

        // AuthenticatedTenantContext reads the tenant claim created by the API-key handler.
        // There is intentionally no TenantId property in BootstrapAccountRequest.
        var tenantId = _tenantContext.TenantId;
        _logger.LogInformation("Bootstrapping local account for tenant {TenantId}", tenantId);
        var result = await _accounts.BootstrapAsync(
            tenantId,
            request.Email,
            request.Password,
            request.DisplayName,
            cancellationToken);

        if (!result.Created)
        {
            // Duplicate/racing account creation is a resource conflict (409). Malformed input
            // and a nonexistent configured tenant are client/configuration errors (400).
            var error = new AuthErrorResponse(result.ErrorCode!, result.ErrorMessage!);
            return result.ErrorCode is "account_exists" or "email_exists" or "account_conflict"
                ? Conflict(error)
                : BadRequest(error);
        }

        var account = result.Account!;
        _logger.LogInformation(
            "Created local account {AccountId} for tenant {TenantId}",
            account.Id,
            account.TenantId);
        // Return only public profile fields. PasswordHash is never part of an API DTO.
        return StatusCode(
            StatusCodes.Status201Created,
            new AccountResponse(
                account.Id,
                account.TenantId,
                account.Email,
                account.DisplayName,
                account.CreatedAtUtc));
    }

    /// <summary>
    /// POST: api/auth/tenants
    /// Provisions a tenant and its first owner account. This is an administrative operation:
    /// only the configured API key may call it, and the server generates the tenant ID.
    /// </summary>
    [HttpPost("tenants")]
    [Authorize(AuthenticationSchemes = ApiKeyAuthenticationHandler.SchemeName)]
    [ProducesResponseType<TenantProvisioningResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<AuthErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<AuthErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<AuthErrorResponse>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> ProvisionTenant(
        [FromBody] ProvisionTenantRequest request,
        CancellationToken cancellationToken)
    {
        // The API-key handler permits keyless legacy mode for ordinary local endpoints. That
        // compatibility behavior must never turn tenant creation into a public operation.
        if (string.IsNullOrWhiteSpace(_configuration["Authentication:ApiKey"]))
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new AuthErrorResponse(
                    "api_key_required",
                    "Configure Authentication:ApiKey before provisioning tenants."));
        }
        if (_multiUser.Mode.IsEnabled)
        {
            // Provisioning would make the all-live-accounts-linked startup invariant false while
            // the process is already admitting tenants. Use an explicit maintenance window:
            // disable, provision/link, re-check readiness, then enable again.
            return Conflict(new AuthErrorResponse(
                "multi_user_provisioning_requires_disabled_mode",
                "Disable guarded multi-user mode before provisioning another tenant."));
        }

        var result = await _accounts.ProvisionTenantAsync(
            request.TenantName,
            request.Email,
            request.Password,
            request.DisplayName,
            cancellationToken);
        if (!result.Created)
        {
            var error = new AuthErrorResponse(result.ErrorCode!, result.ErrorMessage!);
            return result.ErrorCode is "email_exists" or "account_conflict"
                ? Conflict(error)
                : BadRequest(error);
        }

        var tenant = result.Tenant!;
        var account = result.Account!;
        _logger.LogInformation(
            "Provisioned tenant {TenantId} with owner account {AccountId}",
            tenant.Id,
            account.Id);
        return StatusCode(
            StatusCodes.Status201Created,
            new TenantProvisioningResponse(
                tenant.Id,
                tenant.Name,
                tenant.PlatformSyncEnabled,
                new AccountResponse(
                    account.Id,
                    account.TenantId,
                    account.Email,
                    account.DisplayName,
                    account.CreatedAtUtc)));
    }

    /// <summary>
    /// POST: api/auth/token
    /// Exchanges verified local credentials for a signed tenant-scoped bearer token. The
    /// caller presents that token on later requests as "Authorization: Bearer &lt;token&gt;".
    /// </summary>
    [HttpPost("token")]
    // The endpoint must begin anonymously because obtaining a token is the authentication
    // operation itself. AllowAnonymous bypasses [Authorize], not password verification below.
    [AllowAnonymous]
    // Password verification is intentionally rate-limited to reduce online guessing attacks.
    [EnableRateLimiting(AuthRateLimitPolicies.Token)]
    [ProducesResponseType<TokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<AuthErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType<AuthErrorResponse>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Token(
        [FromBody] TokenRequest request,
        CancellationToken cancellationToken)
    {
        // JWT is opt-in. Returning 503 distinguishes missing server configuration from bad
        // user credentials and avoids ever creating an unsigned or weak fallback token.
        if (!_tokens.IsEnabled)
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new AuthErrorResponse(
                    "jwt_not_configured",
                    "JWT authentication is not configured."));
        }

        var account = await _accounts.AuthenticateAsync(
            request.Email,
            request.Password,
            cancellationToken);
        if (account is null)
        {
            // Use exactly the same response for "email not found" and "wrong password" so the
            // endpoint does not reveal which email addresses have accounts.
            _logger.LogWarning("Rejected invalid local account credentials");
            return Unauthorized(new AuthErrorResponse(
                "invalid_credentials",
                "Email or password is invalid."));
        }

        if (account.Tenant?.DeletionRequestedAtUtc is not null)
        {
            // ADR-010 keeps partial deletion retryable after the original access JWT expires.
            // This credential cannot become ordinary access or a refresh session in either mode.
            var deletionToken = _tokens.CreateAccountDeletionToken(account);
            _logger.LogInformation("Issued a restricted pending account-deletion retry session");
            return Ok(CreateRestrictedTokenResponse(
                deletionToken,
                GameHubClaimTypes.AccountDeletionOnlySessionPurpose));
        }

        if (!_multiUser.Mode.IsEnabled && account.TenantId == TenantDefaults.DefaultTenantId)
        {
            // The default tenant keeps the historical local-session behavior while guarded
            // multi-user mode is off. This is the upgrade-safe single-user compatibility path.
            var token = _tokens.CreateToken(account);
            var refreshToken = await _refreshTokens.IssueAsync(account, cancellationToken);
            _logger.LogInformation("Issued a local compatibility session for the default tenant");
            return Ok(CreateTokenResponse(token, refreshToken));
        }

        // Production multi-user mode never turns a password into normal library access. The sole
        // compatibility exception is a ten-minute, non-refreshable JWT for an operator-created
        // account that still needs its first explicit issuer/subject link. Bearer validation
        // permits that signed purpose only on POST /api/auth/oidc/link.
        var mayBootstrapLink = _oidcSettings.IsEnabled
            && !await _externalIdentities.HasIssuerLinkAsync(
                account,
                _oidcSettings.Issuer,
                cancellationToken);
        if (mayBootstrapLink)
        {
            var linkToken = _tokens.CreateOidcLinkToken(account);
            _logger.LogInformation("Issued a restricted OIDC-link bootstrap session");
            return Ok(CreateRestrictedTokenResponse(
                linkToken,
                GameHubClaimTypes.OidcLinkOnlySessionPurpose));
        }

        _logger.LogWarning("Rejected local login outside the single-user compatibility boundary");
        return StatusCode(
            StatusCodes.Status403Forbidden,
            new AuthErrorResponse(
                "local_login_not_allowed",
                "Local password login is not available for this account."));
    }

    /// <summary>
    /// POST: api/auth/oidc/token
    /// Exchanges a provider ID token for a GameHub session only when its exact issuer/subject
    /// pair was explicitly linked to a live account. Provider email and tenant-like claims are
    /// never identity selectors.
    /// </summary>
    [HttpPost("oidc/token")]
    [AllowAnonymous]
    [EnableRateLimiting(AuthRateLimitPolicies.Token)]
    [ProducesResponseType<TokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<AuthErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<AuthErrorResponse>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> OidcToken(
        [FromBody] OidcTokenRequest request,
        CancellationToken cancellationToken)
    {
        // Layer 1: require the independent BFF credential. ID tokens are bearer-like proofs; a
        // public Core exchange would let callers skip the BFF's state/redirect/PKCE transaction.
        if (!IsTrustedOidcBff())
        {
            return Unauthorized(new AuthErrorResponse(
                "oidc_authentication_failed",
                "External authentication failed."));
        }
        // OIDC ultimately creates a normal GameHub session, so local JWT issuance must also be
        // configured. There is no alternate unsigned/cookie-only session format.
        if (!_tokens.IsEnabled)
        {
            return OidcUnavailable("JWT authentication is not configured.");
        }

        // Layer 2: independently validate provider proof. The expected nonce came from the BFF's
        // encrypted transaction; Core still checks it inside the signed ID token.
        var validation = await _oidcTokens.ValidateAsync(
            request.IdToken,
            request.Nonce,
            cancellationToken);
        if (validation.Status is OidcTokenValidationStatus.ProviderUnavailable
            or OidcTokenValidationStatus.ConfigurationUnavailable)
        {
            return OidcUnavailable("OIDC authentication is unavailable.");
        }
        if (validation.Status != OidcTokenValidationStatus.Succeeded)
        {
            return Unauthorized(new AuthErrorResponse(
                "oidc_authentication_failed",
                "External authentication failed."));
        }

        // Layer 3: resolve only the durable issuer/subject mapping. The request has no tenant or
        // account field, and provider email/role/organization claims are not consulted.
        var account = await _externalIdentities.AuthenticateAsync(
            validation.Identity!,
            cancellationToken);
        if (account is null)
        {
            // Unknown, pending-deletion, and deleted mappings intentionally share one response.
            return Unauthorized(new AuthErrorResponse(
                "oidc_authentication_failed",
                "External authentication failed."));
        }

        if (!_multiUser.AllowsOidcLogin(account.TenantId))
        {
            // OIDC readiness and the production switch are separate states. A fully valid linked
            // provider identity still cannot activate a provisioned tenant while the operator
            // switch is false.
            _logger.LogWarning("Rejected a provisioned-tenant login while multi-user mode is disabled");
            return Unauthorized(new AuthErrorResponse(
                "oidc_authentication_failed",
                "External authentication failed."));
        }

        // Layer 4: issue GameHub's own tenant-scoped access token and revocable refresh session.
        // Provider tokens do not authorize library APIs directly.
        var accessToken = _tokens.CreateToken(account);
        var refreshToken = await _refreshTokens.IssueAsync(account, cancellationToken);
        _logger.LogInformation(
            "Issued GameHub session for external identity on account {AccountId} and tenant {TenantId}",
            account.Id,
            account.TenantId);
        return Ok(CreateTokenResponse(accessToken, refreshToken));
    }

    /// <summary>
    /// POST: api/auth/oidc/link
    /// Links a validated provider identity to the currently signed GameHub account. This is the
    /// sole linking path: email similarity never creates or changes a mapping.
    /// </summary>
    [HttpPost("oidc/link")]
    [Authorize]
    [EnableRateLimiting(AuthRateLimitPolicies.Token)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<AuthErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<AuthErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<AuthErrorResponse>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> LinkOidc(
        [FromBody] OidcTokenRequest request,
        CancellationToken cancellationToken)
    {
        // Linking requires both trust channels: the BFF proves the OIDC browser transaction and
        // the current GameHub JWT supplies the only acceptable account/tenant destination.
        if (!IsTrustedOidcBff()
            || !TryGetAuthenticatedIdentity(out var accountId, out var tenantId))
        {
            return Unauthorized(new AuthErrorResponse(
                "account_authentication_required",
                "A current tenant account access token is required."));
        }

        // Validate the new external proof exactly as strictly as a login proof. Linking is not a
        // lower-assurance path and cannot accept unverified profile data.
        var validation = await _oidcTokens.ValidateAsync(
            request.IdToken,
            request.Nonce,
            cancellationToken);
        if (validation.Status is OidcTokenValidationStatus.ProviderUnavailable
            or OidcTokenValidationStatus.ConfigurationUnavailable)
        {
            return OidcUnavailable("OIDC authentication is unavailable.");
        }
        if (validation.Status != OidcTokenValidationStatus.Succeeded)
        {
            return Unauthorized(new AuthErrorResponse(
                "oidc_authentication_failed",
                "External authentication failed."));
        }

        // The service/database enforce uniqueness and pending-deletion checks. Repeated linking of
        // the same exact identity is idempotent; ownership conflicts remain explicit.
        var result = await _externalIdentities.LinkAsync(
            accountId,
            tenantId,
            validation.Identity!,
            cancellationToken);
        if (result == ExternalIdentityLinkResult.Linked)
        {
            _logger.LogInformation(
                "Linked external identity to account {AccountId} and tenant {TenantId}",
                accountId,
                tenantId);
            return NoContent();
        }

        return result == ExternalIdentityLinkResult.AccountUnavailable
            ? Unauthorized(new AuthErrorResponse(
                "account_authentication_required",
                "The current account is unavailable."))
            : Conflict(new AuthErrorResponse(
                "external_identity_conflict",
                "The external identity or provider is already linked."));
    }

    /// <summary>
    /// POST: api/auth/refresh
    /// Consumes an opaque refresh token and rotates it into a new access/refresh token pair.
    /// </summary>
    [HttpPost("refresh")]
    [AllowAnonymous]
    [EnableRateLimiting(AuthRateLimitPolicies.Token)]
    [ProducesResponseType<TokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<AuthErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<AuthErrorResponse>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Refresh(
        [FromBody] RefreshTokenRequest request,
        CancellationToken cancellationToken)
    {
        if (!_tokens.IsEnabled)
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new AuthErrorResponse(
                    "jwt_not_configured",
                    "JWT authentication is not configured."));
        }

        // The refresh secret itself contains no account or tenant claim. RefreshTokenService
        // matches its hash, enforces one-time use, and returns the related account only after
        // successful rotation. That verified row is the trusted source for the next JWT.
        var rotation = await _refreshTokens.RotateAsync(request.RefreshToken, cancellationToken);
        if (!rotation.Rotated)
        {
            _logger.LogWarning("Rejected invalid, expired, revoked, or replayed refresh token");
            return Unauthorized(new AuthErrorResponse(
                "invalid_refresh_token",
                "Refresh token is invalid or expired."));
        }

        var accessToken = _tokens.CreateToken(rotation.Account!);
        _logger.LogInformation(
            "Rotated refresh session for account {AccountId} and tenant {TenantId}",
            rotation.Account!.Id,
            rotation.Account.TenantId);
        return Ok(CreateTokenResponse(accessToken, rotation.RefreshToken!));
    }

    /// <summary>
    /// POST: api/auth/revoke
    /// Revokes an opaque refresh session for logout without revealing whether it existed.
    /// </summary>
    [HttpPost("revoke")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<AuthErrorResponse>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Revoke(
        [FromBody] RefreshTokenRequest request,
        CancellationToken cancellationToken)
    {
        if (!_tokens.IsEnabled)
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new AuthErrorResponse(
                    "jwt_not_configured",
                    "JWT authentication is not configured."));
        }

        // A uniform 204 response prevents token enumeration. Clients may safely discard their
        // local token whether it was active, expired, already revoked, or completely unknown.
        await _refreshTokens.RevokeAsync(request.RefreshToken, cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// PUT: api/auth/password
    /// Changes the signed account's password after checking the current password and revokes
    /// every refresh session for that account.
    /// </summary>
    [HttpPut("password")]
    [Authorize]
    [EnableRateLimiting(AuthRateLimitPolicies.Token)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<AuthErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<AuthErrorResponse>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ChangePassword(
        [FromBody] ChangePasswordRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetAuthenticatedIdentity(out var accountId, out var tenantId))
        {
            return Unauthorized(new AuthErrorResponse(
                "account_authentication_required",
                "A tenant account access token is required."));
        }

        var result = await _accounts.ChangePasswordAsync(
            accountId,
            tenantId,
            request.CurrentPassword,
            request.NewPassword,
            cancellationToken);
        if (!result.Changed)
        {
            var error = new AuthErrorResponse(result.ErrorCode!, result.ErrorMessage!);
            return result.ErrorCode == "account_identity_mismatch"
                ? Unauthorized(error)
                : BadRequest(error);
        }

        _logger.LogInformation(
            "Changed password and revoked refresh sessions for account {AccountId} in tenant {TenantId}",
            accountId,
            tenantId);
        return NoContent();
    }

    /// <summary>
    /// POST: api/auth/revoke-all
    /// Explicitly revokes every refresh session for the signed account.
    /// </summary>
    [HttpPost("revoke-all")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<AuthErrorResponse>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> RevokeAll(CancellationToken cancellationToken)
    {
        if (!TryGetAuthenticatedIdentity(out var accountId, out var tenantId)
            || !await _accounts.RevokeAllSessionsAsync(accountId, tenantId, cancellationToken))
        {
            return Unauthorized(new AuthErrorResponse(
                "account_authentication_required",
                "A current tenant account access token is required."));
        }

        _logger.LogInformation(
            "Revoked all refresh sessions for account {AccountId} in tenant {TenantId}",
            accountId,
            tenantId);
        return NoContent();
    }

    /// <summary>
    /// DELETE: api/auth/account
    /// Idempotently deletes the signed account, its Core tenant data, and delegated Ingest data.
    /// </summary>
    [HttpDelete("account")]
    [Authorize]
    [EnableRateLimiting(AuthRateLimitPolicies.Token)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<AuthErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<AuthErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<AuthErrorResponse>(StatusCodes.Status502BadGateway)]
    [ProducesResponseType<AuthErrorResponse>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> DeleteAccount(
        [FromBody] DeleteAccountRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetAuthenticatedIdentity(out var accountId, out var tenantId))
        {
            return Unauthorized(new AuthErrorResponse(
                "account_authentication_required",
                "A tenant account access token is required."));
        }

        var result = await _deletion.DeleteAsync(
            accountId,
            tenantId,
            request.CurrentPassword,
            cancellationToken);
        return result switch
        {
            AccountDeletionResult.Deleted or AccountDeletionResult.AlreadyDeleted => NoContent(),
            AccountDeletionResult.InvalidCurrentPassword => BadRequest(new AuthErrorResponse(
                "invalid_current_password",
                "Current password is invalid.")),
            AccountDeletionResult.IdentityMismatch => Unauthorized(new AuthErrorResponse(
                "account_identity_mismatch",
                "The authenticated account does not match the tenant.")),
            AccountDeletionResult.IngestCleanupFailed => StatusCode(
                StatusCodes.Status502BadGateway,
                new AuthErrorResponse(
                    "account_deletion_incomplete",
                    "Ingest cleanup did not complete. The account remains pending deletion and the request can be retried.")),
            _ => StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new AuthErrorResponse(
                    "account_deletion_incomplete",
                    "Core cleanup did not complete. The account remains pending deletion and the request can be retried."))
        };
    }

    private bool TryGetAuthenticatedIdentity(out Guid accountId, out Guid tenantId)
    {
        accountId = Guid.Empty;
        tenantId = Guid.Empty;
        return Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out accountId)
            && accountId != Guid.Empty
            && Guid.TryParse(User.FindFirstValue(GameHubClaimTypes.TenantId), out tenantId)
            && tenantId != Guid.Empty
            && tenantId == _tenantContext.TenantId;
    }

    private static TokenResponse CreateTokenResponse(
        IssuedJwtToken accessToken,
        IssuedRefreshToken refreshToken) =>
        new(
            accessToken.AccessToken,
            "Bearer",
            accessToken.ExpiresAtUtc,
            refreshToken.RefreshToken,
            refreshToken.ExpiresAtUtc);

    private static TokenResponse CreateRestrictedTokenResponse(
        IssuedJwtToken accessToken,
        string sessionPurpose) =>
        new(
            accessToken.AccessToken,
            "Bearer",
            accessToken.ExpiresAtUtc,
            null,
            null,
            sessionPurpose);

    private ObjectResult OidcUnavailable(string message) => StatusCode(
        StatusCodes.Status503ServiceUnavailable,
        new AuthErrorResponse("oidc_unavailable", message));

    private bool IsTrustedOidcBff()
    {
        const string headerName = "X-GameHub-Oidc-Bff-Key";
        // Count must be exactly one so proxies cannot create ambiguous comma-joined credentials.
        if (!_oidcSettings.IsEnabled
            || !Request.Headers.TryGetValue(headerName, out var supplied)
            || supplied.Count != 1)
        {
            return false;
        }

        // Avoid ordinary string equality for secret material. Length is checked separately because
        // FixedTimeEquals requires equal-sized inputs for a meaningful constant-work comparison.
        var expectedBytes = Encoding.UTF8.GetBytes(_oidcSettings.BffServiceKey);
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied[0] ?? "");
        return expectedBytes.Length == suppliedBytes.Length
            && CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
    }
}
