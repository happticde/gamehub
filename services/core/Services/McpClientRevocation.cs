using GameHub.Core.Data;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace GameHub.Core.Services;

/// <summary>Closes the exact connection proven by a natively validated integration credential.</summary>
public sealed class McpClientRevocation(CoreContext db, McpGrantAdmission admission,
    TimeProvider clock) : IOpenIddictServerHandler<OpenIddictServerEvents.HandleRevocationRequestContext>
{
    /// <inheritdoc/>
    public async ValueTask HandleAsync(OpenIddictServerEvents.HandleRevocationRequestContext context)
    {
        var principal = context.GenericTokenPrincipal;
        if (context.Request.ClientId is not { } clientId || principal is null
            || !principal.GetPresenters().SequenceEqual([clientId]))
        { context.Reject(Errors.InvalidToken); return; }
        var type = principal.HasTokenType(TokenTypeIdentifiers.RefreshToken)
            ? TokenTypeIdentifiers.RefreshToken : TokenTypeIdentifiers.AccessToken;
        var grant = await admission.AdmitAsync(principal, type, cancellationToken: context.CancellationToken);
        if (grant is null)
        { context.Reject(Errors.InvalidToken); return; }
        await AccountSessionRevocation.RevokeFamilyAsync(db, grant.FamilyId, grant.AccountId,
            grant.TenantId, clock.GetUtcNow().UtcDateTime, context.CancellationToken);
    }
}
