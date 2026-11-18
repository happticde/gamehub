using System.Data;
using GameHub.Core.Data;
using GameHub.Shared.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace GameHub.Core.Services;

/// <summary>
/// One-time device decision. The browser endpoint must first enforce the trusted
/// backend-for-frontend boundary, bounded input, cross-site request forgery protection and
/// session-bound displayed consent. McpBrowserConsent supplies these caller safeguards;
/// this service alone registers no route.
/// </summary>
public sealed class McpDeviceConsent(CoreContext db, McpGrantAdmission admission,
    IOpenIddictTokenManager tokens, McpRestResource resource, TimeProvider clock,
    ILogger<McpDeviceConsent> logger)
{
    private const string Protocol = OpenIddictServerAspNetCoreDefaults.AuthenticationScheme;

    /// <summary>
    /// Executes native verification and commits before returning a bodyless outcome. Ownership
    /// comes only from explicit bearer authentication, never HttpContext.User or request fields.
    /// No library response, code, token or owner identifier is returned to the browser.
    /// </summary>
    public async Task ExecuteAsync(HttpContext http, bool approve, bool allowTrackingWrite = false,
        DateTime? confirmationExpiresAtUtc = null)
    {
        if (db.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("Device consent must own its complete transaction.");
        var output = http.Response.Body;
        await using var buffered = new MemoryStream();
        http.Response.Body = buffered;
        var status = StatusCodes.Status400BadRequest;
        var committed = false;
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, http.RequestAborted);
            if ((confirmationExpiresAtUtc is null || confirmationExpiresAtUtc > clock.GetUtcNow().UtcDateTime)
                && await DecideAsync(http, approve, allowTrackingWrite)
                && (confirmationExpiresAtUtc is null || confirmationExpiresAtUtc > clock.GetUtcNow().UtcDateTime))
            {
                await transaction.CommitAsync(http.RequestAborted);
                committed = true;
                status = StatusCodes.Status204NoContent;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            logger.LogWarning("MCP device decision unavailable ({FailureType}).", exception.GetType().Name);
            status = StatusCodes.Status503ServiceUnavailable;
        }
        finally
        {
            if (!committed) db.ChangeTracker.Clear();
            http.Response.Body = output;
            http.Response.Headers.Remove("Content-Length");
            http.Response.Headers.Remove("Content-Type");
            http.Response.Headers.Remove("Location");
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers.Pragma = "no-cache";
            http.Response.StatusCode = status;
        }
    }

    private async Task<bool> DecideAsync(HttpContext http, bool approve, bool allowTrackingWrite)
    {
        var cancellation = http.RequestAborted;
        var browser = await http.AuthenticateAsync(JwtBearerDefaults.AuthenticationScheme);
        if (!await admission.CanConsentAsync(browser, cancellation)) return false;
        // OpenIddict authenticates the protected user code and supplies its device binding.
        // Its cached principal alone is insufficient: recheck and lock both live stored rows.
        var authentication = await http.AuthenticateAsync(Protocol);
        if (!authentication.Succeeded || authentication.Principal is not { } pending
            || !pending.HasTokenType(TokenTypeIdentifiers.Private.UserCode)
            || pending.GetTokenId() is not { Length: > 0 } userId
            || pending.GetClaim(Claims.Private.DeviceCodeId) is not { Length: > 0 } deviceId
            || !string.IsNullOrEmpty(pending.GetAuthorizationId())
            || pending.HasClaim(claim => claim.Type is Claims.Subject or GameHubClaimTypes.TenantId
                or GameHubClaimTypes.AccountSessionId or GameHubClaimTypes.SessionVersion)
            || !pending.GetPresenters().SequenceEqual([McpDeviceProtocol.ClientId])
            || !pending.GetResources().SequenceEqual([resource.Resource.AbsoluteUri])
            || !pending.GetScopes().Contains(McpPermissions.LibraryRead)
            || pending.GetScopes().Any(scope => scope is not (McpPermissions.LibraryRead
                or McpPermissions.TrackingWrite or Scopes.OfflineAccess))) return false;

        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var (id, type, state) in new[]
        {
            (userId, TokenTypeIdentifiers.Private.UserCode, Statuses.Valid),
            (deviceId, TokenTypeIdentifiers.Private.DeviceCode, Statuses.Inactive)
        })
            if (await db.Set<McpProtocolToken>().Where(row => row.Id == id && row.Type == type
                    && row.Status == state && row.Authorization == null && row.Subject == null
                    && row.ExpirationDate > now && row.Application != null
                    && row.Application.ClientId == McpDeviceProtocol.ClientId)
                .ExecuteUpdateAsync(update => update.SetProperty(row => row.Status, row => row.Status), cancellation) != 1)
                return false;

        // Reload manager-tracked entries after locking; supported manager updates retain their
        // concurrency checks and preserve the protected payload/reference identifier.
        var device = await tokens.FindByIdAsync(deviceId, cancellation) as McpProtocolToken;
        var user = await tokens.FindByIdAsync(userId, cancellation) as McpProtocolToken;
        if (device is null || user is null) return false;
        await db.Entry(device).ReloadAsync(cancellation);
        await db.Entry(user).ReloadAsync(cancellation);
        var pendingEnd = new[] { device.ExpirationDate, user.ExpirationDate,
            pending.GetExpirationDate()?.UtcDateTime }.Min();
        McpGrantAdmissionResult? grant = null;
        if (approve)
        {
            grant = await admission.CreateAsync(browser, pending.GetScopes(), allowTrackingWrite, cancellation);
            if (grant is null) return false;
            var descriptor = new OpenIddictTokenDescriptor();
            await tokens.PopulateAsync(descriptor, device, cancellation);
            descriptor.AuthorizationId = grant.AuthorizationId;
            await tokens.UpdateAsync(device, descriptor, cancellation);

            var principal = grant.CreatePrincipal(Protocol, pending.HasScope(Scopes.OfflineAccess));
            await Results.SignIn(principal, authenticationScheme: Protocol).ExecuteAsync(http);
        }
        else
            await Results.Forbid(new AuthenticationProperties(new Dictionary<string, string?>
            {
                [OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.AccessDenied
            }), [Protocol]).ExecuteAsync(http);

        // Never commit partial library work, even if a late handler rejected an approval or a
        // native TryReject operation lost a race. Re-read database outcomes rather than trackers.
        now = clock.GetUtcNow().UtcDateTime;
        if (pendingEnd is null || pendingEnd <= now || (grant is not null && grant.ExpiresAtUtc <= now)) return false;
        var completedDevice = await db.Set<McpProtocolToken>().AsNoTracking()
            .Include(row => row.Authorization).SingleAsync(row => row.Id == deviceId, cancellation);
        var completedUser = await db.Set<McpProtocolToken>().AsNoTracking()
            .SingleAsync(row => row.Id == userId, cancellation);
        return approve
            ? http.Response.StatusCode is >= 200 and < 300
                && completedUser.Status == Statuses.Redeemed && completedDevice.Status == Statuses.Valid
                && completedDevice.Authorization?.Id == grant!.AuthorizationId
                && completedDevice.Subject == grant.AccountId.ToString("D")
            : completedUser.Status == Statuses.Rejected && completedDevice.Status == Statuses.Rejected
                && completedDevice.Authorization is null;
    }
}
