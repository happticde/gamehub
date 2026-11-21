using GameHub.Core.Data;
using GameHub.Core.Services;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.WebUtilities;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static OpenIddict.Server.OpenIddictServerEvents;

namespace GameHub.Core.Mcp;

internal static class McpRemoteAuthorization
{
    internal static void Register(IServiceCollection services, McpRemoteResource resource)
    {
        services.AddSingleton(resource);
        services.AddSingleton(services => new McpClientMetadata(McpClientMetadata.CreateHttpClient(),
            services.GetRequiredService<TimeProvider>()));
        services.AddScoped<McpRemoteConsent>();
    }

    internal static void Configure(OpenIddictServerBuilder options, Uri issuer, McpRemoteResource resource)
    {
        // The managed route advertises the native-client profile, not the shared device server's defaults.
        options.SetConfigurationEndpointUris(Array.Empty<Uri>());
        options.SetJsonWebKeySetEndpointUris(Array.Empty<Uri>());
        options.SetAuthorizationEndpointUris(McpRemoteResource.AuthorizationPath, McpRemoteResource.ConsentPath);
        options.AllowAuthorizationCodeFlow().RequireProofKeyForCodeExchange();
        options.RegisterResources(resource.Uri);
        options.EnableAuthorizationRequestCaching();
        options.Configure(settings => settings.RequestTokenLifetime = TimeSpan.FromMinutes(5));
        options.SetAuthorizationCodeLifetime(TimeSpan.FromMinutes(2));
        options.UseAspNetCore().EnableAuthorizationEndpointPassthrough();

        options.AddEventHandler<ValidateAuthorizationRequestContext>(builder => builder
            .UseInlineHandler(context =>
            {
                // Native restored requests are subject to the same profile and resource as fresh requests.
                if (McpRemoteClient.Find(context.ClientId) is not { } client
                    || !client.AllowsRedirect(context.Request.RedirectUri, client.RedirectUris)
                    || context.Request.ResponseType != ResponseTypes.Code
                    || (context.Request.ResponseMode is not null and not ResponseModes.Query)
                    || (context.Request.Prompt is not null and not PromptValues.Consent)
                    || context.Request.CodeChallengeMethod != CodeChallengeMethods.Sha256
                    || context.Request.CodeChallenge is not { Length: 43 } challenge || !IsBase64Url(challenge)
                    || string.IsNullOrEmpty(context.Request.State) || !McpDeviceProtocol.AllowedScopes(context.Request.GetScopes())
                    || !McpDeviceProtocol.ExactResource(context.Request, resource.Uri.AbsoluteUri))
                    context.Reject(Errors.InvalidRequest);
                return default;
            }).SetOrder(OpenIddictServerHandlers.Authentication.ValidateClientRedirectUri.Descriptor.Order - 100));

        options.AddEventHandler<HandleAuthorizationRequestContext>(builder => builder.UseInlineHandler(context =>
        {
            var authentication = context.Transaction.GetProperty<ProcessAuthenticationContext>(
                typeof(ProcessAuthenticationContext).FullName!);
            if (authentication?.RequestTokenPrincipal is { } pending)
                context.Transaction.GetHttpRequest()?.HttpContext.Features.Set(new McpPendingRemoteConsent(pending));
            return default;
        }));

        options.AddEventHandler<ApplyAuthorizationResponseContext>(builder => builder.UseInlineHandler(context =>
        {
            if (context.Response.Error is not null || context.Response.RequestUri is not { } pending
                || context.Request?.ClientId is not { } client
                || context.Transaction.GetHttpRequest()?.HttpContext is not { } http
                || http.Request.Path != McpRemoteResource.AuthorizationPath) return default;
            // OpenIddict owns the protected reference and the cached request. The browser
            // receives only the handle and public client ID, never a client callback here.
            http.Response.Redirect(QueryHelpers.AddQueryString(new Uri(issuer, McpRemoteResource.BrowserPath).AbsoluteUri,
                new Dictionary<string, string?> { [Parameters.ClientId] = client, [Parameters.RequestUri] = pending }));
            http.Features.Set(new McpRemoteReply(StatusCodes.Status302Found));
            context.HandleRequest();
            return default;
        }).SetOrder(200_000));
    }

    internal static async Task<bool> EnsureRegistrationAsync(IOpenIddictApplicationManager applications,
        McpRemoteClient client, McpRemoteResource resource, CancellationToken cancellationToken)
    {
        var definition = new OpenIddictApplicationDescriptor
        {
            ClientId = client.Id, DisplayName = client.Name, ApplicationType = ApplicationTypes.Native,
            ClientType = ClientTypes.Public, ConsentType = ConsentTypes.Explicit,
            Permissions =
            {
                Permissions.Endpoints.Authorization, Permissions.Endpoints.Token, Permissions.Endpoints.Revocation,
                Permissions.GrantTypes.AuthorizationCode, Permissions.GrantTypes.RefreshToken, Permissions.ResponseTypes.Code,
                Permissions.Prefixes.Resource + resource.Uri.AbsoluteUri,
                Permissions.Prefixes.Scope + McpPermissions.LibraryRead,
                Permissions.Prefixes.Scope + McpPermissions.TrackingWrite
            },
            Requirements = { Requirements.Features.ProofKeyForCodeExchange }
        };
        definition.RedirectUris.UnionWith(client.RedirectUris.Select(value => new Uri(value)));
        var existing = await applications.FindByClientIdAsync(client.Id, cancellationToken);
        if (existing is null)
        {
            await applications.CreateAsync(definition, cancellationToken);
            return true;
        }
        var stored = new OpenIddictApplicationDescriptor();
        await applications.PopulateAsync(stored, existing, cancellationToken);
        return stored.ApplicationType == definition.ApplicationType && stored.ClientType == definition.ClientType
            && stored.ConsentType == definition.ConsentType && stored.ClientSecret is null && stored.JsonWebKeySet is null
            && stored.Settings.Count == 0 && stored.PostLogoutRedirectUris.Count == 0
            && stored.Permissions.SetEquals(definition.Permissions)
            && stored.Requirements.SetEquals(definition.Requirements)
            && stored.RedirectUris.Select(uri => uri.AbsoluteUri).ToHashSet(StringComparer.Ordinal).SetEquals(client.RedirectUris);
    }

    internal static bool IsBase64Url(string value) =>
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
}

internal sealed record McpPendingRemoteConsent(System.Security.Claims.ClaimsPrincipal Principal);
internal sealed record McpRemoteReply(int Status);
