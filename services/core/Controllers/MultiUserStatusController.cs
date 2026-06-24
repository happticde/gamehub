using GameHub.Core.Models.Auth;
using GameHub.Core.Services;
using GameHub.Shared.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GameHub.Core.Controllers;

/// <summary>
/// Operator-only, secret-free guarded multi-user readiness surface. It intentionally reports no
/// account counts, identifiers, tenant names, provider tokens, configured values, or key material.
/// </summary>
[ApiController]
[Route("api/auth/multi-user")]
public sealed class MultiUserStatusController(
    MultiUserEnablementService enablement,
    OidcSettings oidcSettings,
    IConfiguration configuration) : ControllerBase
{
    /// <summary>Returns safe enablement state and blocker codes to an authenticated operator.</summary>
    [HttpGet("status")]
    [Authorize(AuthenticationSchemes = ApiKeyAuthenticationHandler.SchemeName)]
    [ProducesResponseType<MultiUserStatusResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<AuthErrorResponse>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetStatus(CancellationToken cancellationToken)
    {
        // The compatibility API-key handler deliberately authenticates when no key is configured.
        // Readiness is operator-only, so this endpoint repeats the bootstrap/provisioning guard.
        if (string.IsNullOrWhiteSpace(configuration["Authentication:ApiKey"]))
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new AuthErrorResponse(
                    "operator_authentication_not_configured",
                    "Operator authentication is not configured."));
        }

        var readiness = await enablement.EvaluateAsync(cancellationToken);
        Response.Headers.CacheControl = "no-store";
        return Ok(new MultiUserStatusResponse(
            readiness.OidcEnabled,
            oidcSettings.ProvisioningMode.ToSafeLabel(),
            readiness.ReadinessSatisfied,
            readiness.MultiUserEnabled,
            readiness.MultiUserEnabled && readiness.ReadinessSatisfied,
            readiness.MultiUserEnabled ? "oidc_link_only" : "compatibility_login",
            readiness.Blockers));
    }
}
