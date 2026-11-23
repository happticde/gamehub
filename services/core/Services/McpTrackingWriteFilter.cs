using System.Security.Claims;
using System.Runtime.ExceptionServices;
using GameHub.Core.Data;
using GameHub.Shared.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using OpenIddict.Abstractions;

namespace GameHub.Core.Services;

// Only the integration authentication handler creates this after protocol and live validation.
// Never build it from HttpContext.User, a header, or browser-submitted claims.
internal sealed class McpValidatedIdentityFeature(ClaimsPrincipal principal)
{
    internal ClaimsPrincipal Principal { get; } = principal.Clone();

    internal bool MatchesRequest(ClaimsPrincipal request, string scheme)
    {
        if (request.Identities.ToArray() is not [{ IsAuthenticated: true } identity]
            || identity.AuthenticationType != scheme) return false;
        static string? One(ClaimsPrincipal principal, string type) =>
            principal.FindAll(type).Select(claim => claim.Value).ToArray() is [var value] ? value : null;
        foreach (var type in new[] { OpenIddictConstants.Claims.Subject,
                     GameHubClaimTypes.TenantId, GameHubClaimTypes.AccountSessionId, GameHubClaimTypes.SessionVersion })
            if (One(Principal, type) is not { } value || One(request, type) != value) return false;
        return One(request, ClaimTypes.NameIdentifier) == One(Principal, OpenIddictConstants.Claims.Subject)
            && request.GetScopes().SequenceEqual(Principal.GetScopes());
    }
}

/// <summary>Commits personal tracking and final integration admission in one database transaction.</summary>
public sealed class McpTrackingWriteFilter(CoreContext db, ILogger<McpTrackingWriteFilter> logger)
    : IAsyncActionFilter
{
    /// <summary>Runs after authorization and before the action or its success response executes.</summary>
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var http = context.HttpContext;
        if (!http.User.Identities.Any(identity => identity.IsAuthenticated
                && identity.AuthenticationType == McpAuthorization.IntegrationScheme))
        {
            await next();
            return;
        }

        // A reconstructed library principal omits the token/authorization/resource metadata.
        // It cannot substitute for the original, successfully validated protocol identity.
        var validated = http.Features.Get<McpValidatedIdentityFeature>();
        if (validated is null || !validated.MatchesRequest(http.User, McpAuthorization.IntegrationScheme))
        {
            context.Result = new ForbidResult(McpAuthorization.IntegrationScheme);
            return;
        }

        var committed = false;
        ActionExecutedContext? executed = null;
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(http.RequestAborted);
            var admission = http.RequestServices.GetRequiredService<McpGrantAdmission>();
            if (await admission.AdmitAsync(validated.Principal, OpenIddictConstants.TokenTypeIdentifiers.AccessToken,
                    McpPermissions.TrackingWrite, http.RequestAborted) is null)
            {
                context.Result = new ForbidResult(McpAuthorization.IntegrationScheme);
                return;
            }

            executed = await next();
            // This action returns MVC results; they execute only after this filter returns.
            // An error, cancellation or exception never commits the mutation or exposes success.
            if (executed.Exception is { } failure)
            {
                if (failure is OperationCanceledException) ExceptionDispatchInfo.Capture(failure).Throw();
                logger.LogWarning("MCP tracking write unavailable ({FailureType}).", failure.GetType().Name);
                executed.ExceptionHandled = true;
                executed.Result = new StatusCodeResult(StatusCodes.Status503ServiceUnavailable);
                return;
            }
            if (executed.Result is not ObjectResult { StatusCode: >= 200 and < 300 }) return;
            if (executed.Canceled)
            {
                executed.Result = new StatusCodeResult(StatusCodes.Status503ServiceUnavailable);
                return;
            }
            http.RequestAborted.ThrowIfCancellationRequested();
            await transaction.CommitAsync(http.RequestAborted);
            committed = true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            logger.LogWarning("MCP tracking write unavailable ({FailureType}).", exception.GetType().Name);
            var unavailable = new StatusCodeResult(StatusCodes.Status503ServiceUnavailable);
            if (executed is null) context.Result = unavailable;
            else executed.Result = unavailable;
        }
        finally
        {
            // Rollback does not reset tracked entities. No later filter may save partial work.
            if (!committed) db.ChangeTracker.Clear();
        }
    }
}
