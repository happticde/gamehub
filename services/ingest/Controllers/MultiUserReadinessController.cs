using GameHub.Ingest.Services;
using GameHub.Shared.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GameHub.Ingest.Controllers;

/// <summary>
/// Internal secret-free handshake proving that Core's delegation key reaches the configured
/// Ingest process and that its guarded mode/vault state can support the same rollout.
/// </summary>
[ApiController]
[Route("api/internal/multi-user")]
[Authorize(AuthenticationSchemes = TenantDelegationAuthenticationHandler.SchemeName)]
public sealed class MultiUserReadinessController(
    MultiUserMode multiUserMode,
    PlatformCredentialKeyRing keyRing) : ControllerBase
{
    /// <summary>Returns only safe process state; delegated tenant data is never queried.</summary>
    [HttpGet("readiness")]
    [ProducesResponseType<IngestMultiUserReadinessResponse>(StatusCodes.Status200OK)]
    public IActionResult GetReadiness()
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(new IngestMultiUserReadinessResponse(
            multiUserMode.IsEnabled,
            keyRing.IsConfigured));
    }
}

/// <summary>Secret-free Ingest state consumed only through trusted Core delegation.</summary>
public sealed record IngestMultiUserReadinessResponse(
    bool MultiUserEnabled,
    bool CredentialVaultReady);
