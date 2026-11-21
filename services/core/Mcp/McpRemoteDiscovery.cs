using GameHub.Core.Data;
using GameHub.Core.Services;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace GameHub.Core.Mcp;

internal static class McpRemoteDiscovery
{
    internal const string MetadataPrefix = "/.well-known/oauth";
    internal const string ResourceMetadataPath = MetadataPrefix + "-protected-resource" + McpRemoteResource.Path;
    internal const string ServerMetadataPath = MetadataPrefix + "-authorization-server";

    internal static void MapMcpRemoteDiscovery(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(ResourceMetadataPath, GetResourceMetadata).AllowAnonymous();
        endpoints.MapGet(ServerMetadataPath, GetServerMetadata).AllowAnonymous();
    }

    /// <summary>GET /.well-known/oauth-protected-resource/mcp advertises the minimum read permission.</summary>
    private static IResult GetResourceMetadata(McpRemoteResource resource) => Results.Json(new
    {
        resource = resource.Uri.AbsoluteUri,
        authorization_servers = new[] { resource.Issuer.AbsoluteUri },
        scopes_supported = new[] { McpPermissions.LibraryRead },
        bearer_methods_supported = new[] { "header" }
    });

    /// <summary>GET /.well-known/oauth-authorization-server describes the two supported native clients' flow.</summary>
    private static IResult GetServerMetadata(McpRemoteResource resource) => Results.Json(new
    {
        issuer = resource.Issuer.AbsoluteUri,
        authorization_endpoint = new Uri(resource.Issuer, McpRemoteResource.AuthorizationPath).AbsoluteUri,
        token_endpoint = new Uri(resource.Issuer, McpDeviceProtocol.TokenPath).AbsoluteUri,
        scopes_supported = new[] { McpPermissions.LibraryRead, McpPermissions.TrackingWrite, Scopes.OfflineAccess },
        response_types_supported = new[] { ResponseTypes.Code },
        response_modes_supported = new[] { ResponseModes.Query },
        grant_types_supported = new[] { GrantTypes.AuthorizationCode, GrantTypes.RefreshToken },
        code_challenge_methods_supported = new[] { CodeChallengeMethods.Sha256 },
        token_endpoint_auth_methods_supported = new[] { ClientAuthenticationMethods.None },
        authorization_response_iss_parameter_supported = true,
        client_id_metadata_document_supported = true
    });

    internal static void SetReadChallenge(HttpResponse response, McpRemoteResource resource, string? error = null)
    {
        var metadata = new Uri(resource.Issuer, ResourceMetadataPath).AbsoluteUri;
        var challenge = $"Bearer resource_metadata=\"{metadata}\", scope=\"{McpPermissions.LibraryRead}\"";
        response.Headers.WWWAuthenticate = error is null ? challenge : $"{challenge}, error=\"{error}\"";
    }
}
