using GameHub.Core.Models.PlatformCredentials;
using GameHub.Core.Services;
using GameHub.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GameHub.Core.Controllers;

/// <summary>Tenant-scoped management of encrypted platform credentials through Core.</summary>
[ApiController]
[Route("api/platform-credentials")]
[Authorize]
public sealed class PlatformCredentialsController(
    PlatformCredentialManagementService credentials,
    ILogger<PlatformCredentialsController> logger) : ControllerBase
{
    /// <summary>
    /// GET: api/platform-credentials
    /// Returns secret-free credential status and the active tenant's derived sync state.
    /// </summary>
    [HttpGet]
    [ProducesResponseType<PlatformCredentialManagementResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<PlatformCredentialErrorResponse>(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<PlatformCredentialManagementResponse>> List(
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await credentials.GetStatusAsync(cancellationToken));
        }
        catch (IngestPlatformCredentialException exception)
        {
            return UpstreamFailure(exception);
        }
        catch (HttpRequestException exception)
        {
            return TransportFailure(exception);
        }
        catch (InvalidOperationException exception)
        {
            return ConfigurationFailure(exception);
        }
    }

    /// <summary>
    /// GET: api/platform-credentials/retrieval-history
    /// Returns tenant-scoped stored-retrieval audit entries through Core delegation.
    /// </summary>
    [HttpGet("retrieval-history")]
    [ProducesResponseType<IReadOnlyList<PlatformRetrievalAuditLogResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<PlatformRetrievalAuditLogResponse>>> GetRetrievalHistory(
        [FromQuery] string? platform,
        [FromQuery] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return Ok(await credentials.GetRetrievalHistoryAsync(platform, limit, cancellationToken));
        }
        catch (IngestPlatformCredentialException exception)
        {
            return UpstreamFailure(exception);
        }
        catch (HttpRequestException exception)
        {
            return TransportFailure(exception);
        }
        catch (InvalidOperationException exception)
        {
            return ConfigurationFailure(exception);
        }
    }

    /// <summary>
    /// PUT: api/platform-credentials/{platform}
    /// Stores a tenant credential and enables sync only after server-side vault verification.
    /// </summary>
    [HttpPut("{platform}")]
    [ProducesResponseType<PlatformCredentialManagementResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<PlatformCredentialErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<PlatformCredentialErrorResponse>(StatusCodes.Status502BadGateway)]
    [ProducesResponseType<PlatformCredentialErrorResponse>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<PlatformCredentialManagementResponse>> Upsert(
        Platform platform,
        [FromBody] UpsertPlatformCredentialRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await credentials.UpsertAsync(platform, request, cancellationToken));
        }
        catch (UnsupportedPlatformCredentialException exception)
        {
            return BadRequest(new PlatformCredentialErrorResponse(
                "unsupported_platform",
                exception.Message));
        }
        catch (IngestPlatformCredentialException exception)
        {
            return UpstreamFailure(exception);
        }
        catch (HttpRequestException exception)
        {
            return TransportFailure(exception);
        }
        catch (InvalidOperationException exception)
        {
            return ConfigurationFailure(exception);
        }
    }

    /// <summary>
    /// DELETE: api/platform-credentials/{platform}
    /// Deletes a credential while keeping sync disabled unless another verified credential remains.
    /// </summary>
    [HttpDelete("{platform}")]
    [ProducesResponseType<PlatformCredentialManagementResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<PlatformCredentialErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<PlatformCredentialErrorResponse>(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<PlatformCredentialManagementResponse>> Delete(
        Platform platform,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await credentials.DeleteAsync(platform, cancellationToken));
        }
        catch (UnsupportedPlatformCredentialException exception)
        {
            return BadRequest(new PlatformCredentialErrorResponse(
                "unsupported_platform",
                exception.Message));
        }
        catch (IngestPlatformCredentialException exception)
        {
            return UpstreamFailure(exception);
        }
        catch (HttpRequestException exception)
        {
            return TransportFailure(exception);
        }
        catch (InvalidOperationException exception)
        {
            return ConfigurationFailure(exception);
        }
    }

    /// <summary>
    /// POST: api/platform-credentials/{platform}/retrieve
    /// Fetches fresh raw platform data using the active tenant's stored encrypted credential.
    /// </summary>
    [HttpPost("{platform}/retrieve")]
    [ProducesResponseType<PlatformRetrievalResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<PlatformCredentialErrorResponse>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<PlatformRetrievalResponse>> Retrieve(
        Platform platform,
        CancellationToken cancellationToken)
    {
        try
        {
            await credentials.RetrieveAsync(platform, cancellationToken);
            return Ok(new PlatformRetrievalResponse(
                platform,
                $"{platform} retrieval completed successfully."));
        }
        catch (UnsupportedPlatformCredentialException exception)
        {
            return BadRequest(new PlatformCredentialErrorResponse(
                "unsupported_platform",
                exception.Message));
        }
        catch (TenantPlatformSyncDisabledException)
        {
            return Conflict(new PlatformCredentialErrorResponse(
                "platform_sync_disabled",
                "Configure a supported credential before retrieving platform data."));
        }
        catch (PlatformCredentialMissingException exception)
        {
            return Conflict(new PlatformCredentialErrorResponse(
                "credential_missing",
                exception.Message));
        }
        catch (IngestPlatformCredentialException exception)
        {
            return UpstreamFailure(exception);
        }
        catch (HttpRequestException exception)
        {
            return TransportFailure(exception);
        }
        catch (InvalidOperationException exception)
        {
            return ConfigurationFailure(exception);
        }
    }

    private ObjectResult UpstreamFailure(IngestPlatformCredentialException exception)
    {
        var statusCode = exception.UpstreamStatusCode switch
        {
            System.Net.HttpStatusCode.BadRequest => StatusCodes.Status400BadRequest,
            System.Net.HttpStatusCode.ServiceUnavailable => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status502BadGateway
        };
        var code = statusCode switch
        {
            StatusCodes.Status400BadRequest => "invalid_credential",
            StatusCodes.Status503ServiceUnavailable => "credential_vault_unavailable",
            _ => "ingest_unavailable"
        };

        logger.LogWarning(
            "Platform credential request failed in Ingest with status {StatusCode}",
            (int)exception.UpstreamStatusCode);
        return StatusCode(
            statusCode,
            new PlatformCredentialErrorResponse(code, exception.SafeMessage));
    }

    private ObjectResult TransportFailure(HttpRequestException exception)
    {
        logger.LogWarning(exception, "Could not reach Ingest for a platform credential request");
        return StatusCode(
            StatusCodes.Status502BadGateway,
            new PlatformCredentialErrorResponse(
                "ingest_unavailable",
                "The Ingest service could not complete the credential request."));
    }

    private ObjectResult ConfigurationFailure(InvalidOperationException exception)
    {
        logger.LogError(exception, "Platform credential management is not configured correctly");
        return StatusCode(
            StatusCodes.Status503ServiceUnavailable,
            new PlatformCredentialErrorResponse(
                "credential_management_unavailable",
                "Platform credential management is not configured on the server."));
    }
}
