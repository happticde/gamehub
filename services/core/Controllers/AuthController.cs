using System.Security.Claims;
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
    private readonly AccountDeletionService _deletion;
    private readonly ITenantContext _tenantContext;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AuthController> _logger;

    /// <summary>Initializes the authentication API with scoped account and tenant services.</summary>
    public AuthController(
        AccountService accounts,
        JwtTokenService tokens,
        RefreshTokenService refreshTokens,
        AccountDeletionService deletion,
        ITenantContext tenantContext,
        IConfiguration configuration,
        ILogger<AuthController> logger)
    {
        _accounts = accounts;
        _tokens = tokens;
        _refreshTokens = refreshTokens;
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

        // AuthenticateAsync returns a database account only after password verification.
        // JwtTokenService copies the tenant from that trusted account and signs the result.
        var token = _tokens.CreateToken(account);
        // The JWT is self-contained and short-lived. The independent opaque refresh secret
        // creates a revocable server-side session and is returned only in this response.
        var refreshToken = await _refreshTokens.IssueAsync(account, cancellationToken);
        _logger.LogInformation(
            "Issued JWT for account {AccountId} and tenant {TenantId}",
            account.Id,
            account.TenantId);
        return Ok(CreateTokenResponse(token, refreshToken));
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
}
