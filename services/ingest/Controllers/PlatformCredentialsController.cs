using GameHub.Ingest.Models;
using GameHub.Ingest.Services;
using GameHub.Shared;
using GameHub.Shared.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GameHub.Ingest.Controllers;

/// <summary>Internal, tenant-delegated administration of encrypted platform credentials.</summary>
[ApiController]
[Route("api/internal/platform-credentials")]
[Authorize(AuthenticationSchemes = TenantDelegationAuthenticationHandler.SchemeName)]
public sealed class PlatformCredentialsController(
    PlatformCredentialService credentials,
    ILogger<PlatformCredentialsController> logger) : ControllerBase
{
    /// <summary>
    /// GET: api/internal/platform-credentials
    /// Returns secret-free configuration state for the delegated tenant.
    /// </summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<PlatformCredentialStatusResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<PlatformCredentialStatusResponse>>> List(
        CancellationToken cancellationToken)
    {
        var statuses = await credentials.ListStatusAsync(cancellationToken);
        logger.LogDebug("Listed encrypted platform credential status");
        return Ok(statuses);
    }

    /// <summary>
    /// PUT: api/internal/platform-credentials/{platform}
    /// Encrypts and replaces one credential without returning its plaintext.
    /// </summary>
    [HttpPut("{platform}")]
    [ProducesResponseType<PlatformCredentialStatusResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<PlatformCredentialErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<PlatformCredentialErrorResponse>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<PlatformCredentialStatusResponse>> Upsert(
        Platform platform,
        [FromBody] UpsertPlatformCredentialRequest request,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(platform))
        {
            return BadRequest(new PlatformCredentialErrorResponse("Platform is unsupported."));
        }

        try
        {
            var status = await credentials.UpsertAsync(
                platform,
                request.SchemaVersion,
                request.Credentials,
                cancellationToken);
            logger.LogInformation("Stored encrypted platform credential for {Platform}", platform);
            return Ok(status);
        }
        catch (PlatformCredentialValidationException ex)
        {
            return BadRequest(new PlatformCredentialErrorResponse(ex.Message));
        }
        catch (PlatformCredentialVaultNotConfiguredException)
        {
            logger.LogWarning(
                "Rejected platform credential write because the credential vault is not configured");
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new PlatformCredentialErrorResponse("Platform credential storage is unavailable."));
        }
    }

    /// <summary>
    /// DELETE: api/internal/platform-credentials/{platform}
    /// Deletes one credential belonging to the delegated tenant.
    /// </summary>
    [HttpDelete("{platform}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<PlatformCredentialErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(
        Platform platform,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(platform))
        {
            return BadRequest(new PlatformCredentialErrorResponse("Platform is unsupported."));
        }

        if (!await credentials.DeleteAsync(platform, cancellationToken))
        {
            return NotFound();
        }

        logger.LogInformation("Deleted encrypted platform credential for {Platform}", platform);
        return NoContent();
    }
}
