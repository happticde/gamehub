using GameHub.Core.Data;
using GameHub.Core.Models.Auth;
using GameHub.Core.Services;
using GameHub.Shared.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace GameHub.Core.Controllers;

/// <summary>
/// Bootstraps local tenant credentials and exchanges them for JWT access tokens.
///
/// The two endpoints have deliberately different trust models:
///   - bootstrap trusts the pre-existing server API key to choose a tenant;
///   - token starts anonymous, verifies email/password, then signs the account's tenant ID.
/// At no point does a request body get to submit or override a tenant identifier.
/// </summary>
[ApiController]
[Route("api/auth")]
public sealed class AuthController : ControllerBase
{
    private readonly AccountService _accounts;
    private readonly JwtTokenService _tokens;
    private readonly ITenantContext _tenantContext;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AuthController> _logger;

    /// <summary>Initializes the authentication API with scoped account and tenant services.</summary>
    public AuthController(
        AccountService accounts,
        JwtTokenService tokens,
        ITenantContext tenantContext,
        IConfiguration configuration,
        ILogger<AuthController> logger)
    {
        _accounts = accounts;
        _tokens = tokens;
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
        _logger.LogInformation(
            "Issued JWT for account {AccountId} and tenant {TenantId}",
            account.Id,
            account.TenantId);
        return Ok(new TokenResponse(token.AccessToken, "Bearer", token.ExpiresAtUtc));
    }
}
