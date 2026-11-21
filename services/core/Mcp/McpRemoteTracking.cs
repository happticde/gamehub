using GameHub.Core.Data;
using GameHub.Core.Services;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace GameHub.Core.Mcp;

internal sealed class McpRemoteTracking(CoreContext db, McpGrantAdmission admission,
    IHttpContextAccessor requests, ILogger<McpRemoteTracking> logger)
{
    internal async Task<PersonalTrackingResult?> WriteAsync(
        Func<Task<PersonalTrackingResult?>> write, CancellationToken cancellationToken)
    {
        var http = requests.HttpContext;
        var validated = http?.Features.Get<McpValidatedIdentityFeature>();
        if (http is null || validated is null
            || !validated.MatchesRequest(http.User, PrivateMcpTransport.Scheme))
            throw new McpException("Tracking permission is unavailable.");

        var committed = false;
        try
        {
            // Lock the same durable owner/grant rows used by refresh and revocation. The SDK
            // receives a result only after the tenant-scoped mutation commits.
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            if (await admission.AdmitAsync(validated.Principal, TokenTypeIdentifiers.AccessToken,
                    McpPermissions.TrackingWrite, cancellationToken) is null)
                throw new McpException("Tracking permission is unavailable.");

            var result = await write();
            if (result is null) return null;
            cancellationToken.ThrowIfCancellationRequested();
            await transaction.CommitAsync(cancellationToken);
            committed = true;
            return result;
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or McpException))
        {
            logger.LogWarning("Remote tracking write unavailable ({FailureType}).", exception.GetType().Name);
            throw new McpException("Tracking update is unavailable.");
        }
        finally
        {
            if (!committed) db.ChangeTracker.Clear();
        }
    }
}
