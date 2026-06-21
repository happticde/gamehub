using GameHub.Ingest.Services;
using GameHub.Shared.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GameHub.Ingest.Controllers;

/// <summary>Internal tenant lifecycle operations callable only through Core delegation.</summary>
[ApiController]
[Route("api/internal/tenant")]
[Authorize(AuthenticationSchemes = TenantDelegationAuthenticationHandler.SchemeName)]
public sealed class TenantLifecycleController(
    TenantDataDeletionService deletion,
    ILogger<TenantLifecycleController> logger) : ControllerBase
{
    /// <summary>
    /// DELETE: api/internal/tenant
    /// Idempotently deletes all Ingest data for the tenant selected by trusted delegation.
    /// </summary>
    [HttpDelete]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(CancellationToken cancellationToken)
    {
        await deletion.DeleteCurrentTenantAsync(cancellationToken);
        logger.LogInformation("Deleted all data for the delegated Ingest tenant");
        return NoContent();
    }
}
