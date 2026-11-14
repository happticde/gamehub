using System.IdentityModel.Tokens.Jwt;
using System.Collections.Immutable;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using OpenIddict.Server.AspNetCore;
using OpenIddict.Validation.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace GameHub.OpenIddictProof;

// Test-only server. There is no production service reference, listener, or durable database.
internal sealed class ProofHost : IAsyncDisposable
{
    internal const string Resource = "https://gamehub.example.com/api";
    internal const string RemoteResource = "https://gamehub.example.com/mcp";
    internal const string ClientId = "gamehub-mcp-stdio";
    internal const string Integration = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme;
    internal const string Protocol = OpenIddictServerAspNetCoreDefaults.AuthenticationScheme;
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly SymmetricSecurityKey browserKey = new(RandomNumberGenerator.GetBytes(32));
    internal WebApplication App { get; private set; } = null!;
    internal HttpClient Client { get; private set; } = null!;

    internal async Task StartAsync()
    {
        await connection.OpenAsync();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Production", Args = []
        });
        builder.Configuration.Sources.Clear();
        // Protocol logs can contain codes/tokens on older library versions. Emit only check names.
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Services.AddDbContext<ProofDatabase>(o => o.UseSqlite(connection).UseOpenIddict());
        builder.Services.AddAuthentication().AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, o =>
        {
            o.MapInboundClaims = false;
            o.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true, IssuerSigningKey = browserKey,
                ValidateIssuer = true, ValidIssuer = "GameHub.Core",
                ValidateAudience = true, ValidAudience = "GameHub.Clients",
                ValidateLifetime = true, ClockSkew = TimeSpan.FromMinutes(1),
                ValidAlgorithms = [SecurityAlgorithms.HmacSha256]
            };
        });
        builder.Services.AddAuthorization(o =>
        {
            o.AddPolicy("browser", p => p.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser());
            o.AddPolicy("integration", p => p.AddAuthenticationSchemes(Integration).RequireAuthenticatedUser());
        });
        builder.Services.AddOpenIddict()
            .AddCore(o => o.UseEntityFrameworkCore().UseDbContext<ProofDatabase>())
            .AddServer(o =>
            {
                o.SetIssuer(new Uri("https://issuer.example.com/"));
                o.SetDeviceAuthorizationEndpointUris("connect/device");
                o.SetEndUserVerificationEndpointUris("connect/verify");
                o.SetTokenEndpointUris("connect/token");
                o.SetRevocationEndpointUris("connect/revoke");
                o.AllowDeviceAuthorizationFlow().AllowRefreshTokenFlow();
                o.RegisterScopes("library:read", "tracking:write");
                o.RegisterResources(new Uri(Resource), new Uri(RemoteResource));
                o.SetRefreshTokenReuseLeeway(null);
                o.AddEphemeralEncryptionKey().AddEphemeralSigningKey();
                o.UseAspNetCore().EnableEndUserVerificationEndpointPassthrough().EnableTokenEndpointPassthrough();
                // Additional GameHub policy through supported extension points. Built-in checks stay on.
                o.AddEventHandler<OpenIddictServerEvents.ValidateDeviceAuthorizationRequestContext>(b =>
                    b.UseInlineHandler(c =>
                    {
                        if (!HasExactResource(c.Request)) c.Reject(Errors.InvalidTarget);
                        return default;
                    }).SetOrder(int.MaxValue - 100));
                o.AddEventHandler<OpenIddictServerEvents.HandleDeviceAuthorizationRequestContext>(b =>
                    b.UseInlineHandler(c =>
                    {
                        c.Principal = new ClaimsPrincipal(new ClaimsIdentity());
                        c.Principal.SetScopes(c.Request.GetScopes()).SetResources(Resource);
                        return default;
                    }));
                o.AddEventHandler<OpenIddictServerEvents.ValidateTokenRequestContext>(b =>
                    b.UseInlineHandler(c =>
                    {
                        if (!HasExactResource(c.Request)) c.Reject(Errors.InvalidTarget);
                        return default;
                    }).SetOrder(int.MaxValue - 100));
            })
            .AddValidation(o =>
            {
                o.UseLocalServer();
                o.UseAspNetCore();
                o.AddAudiences(Resource);
                o.EnableTokenEntryValidation();
                o.EnableAuthorizationEntryValidation();
            });
        App = builder.Build();
        App.UseAuthentication();
        App.UseAuthorization();
        App.MapGet("/browser", () => Results.Ok()).RequireAuthorization("browser");
        App.MapGet("/integration", (HttpContext http) => Results.Json(new
        {
            tokenId = http.User.GetTokenId(), authorizationId = http.User.GetAuthorizationId(),
            audiences = http.User.GetAudiences(), scopes = http.User.GetScopes()
        })).RequireAuthorization("integration");
        App.MapPost("/connect/verify", ApproveAsync);
        App.MapPost("/connect/token", (Delegate)ExchangeAsync);
        await using (var scope = App.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ProofDatabase>().Database.EnsureCreatedAsync();
            var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
            await manager.CreateAsync(new OpenIddictApplicationDescriptor
            {
                ClientId = ClientId, ClientType = ClientTypes.Public, ConsentType = ConsentTypes.Explicit,
                Permissions =
                {
                    Permissions.Endpoints.DeviceAuthorization, Permissions.Endpoints.Token,
                    Permissions.Endpoints.Revocation, Permissions.GrantTypes.DeviceCode,
                    Permissions.GrantTypes.RefreshToken, Permissions.Prefixes.Scope + "library:read",
                    Permissions.Prefixes.Scope + "tracking:write",
                    // Deliberately register both to exercise the stricter per-grant resource policy.
                    Permissions.Prefixes.Resource + Resource, Permissions.Prefixes.Resource + RemoteResource
                }
            });
        }
        await App.StartAsync();
        Client = App.GetTestClient();
        Client.BaseAddress = new Uri("https://issuer.example.com/");
    }

    private static bool HasExactResource(OpenIddictRequest request)
    {
        var values = (ImmutableArray<string?>)request[Parameters.Resource];
        return values is [Resource];
    }

    private static async Task<IResult> ApproveAsync(HttpContext http, IOpenIddictAuthorizationManager authorizations,
        IOpenIddictApplicationManager applications)
    {
        var browser = await http.AuthenticateAsync(JwtBearerDefaults.AuthenticationScheme);
        if (!browser.Succeeded) return Results.Unauthorized();
        var code = await http.AuthenticateAsync(Protocol);
        if (!code.Succeeded) return Results.Unauthorized();
        var identity = new ClaimsIdentity("proof-consent");
        var principal = new ClaimsPrincipal(identity);
        identity.SetClaim(Claims.Subject, browser.Principal!.FindFirst("sub")!.Value);
        principal.SetScopes(code.Principal!.GetScopes()).SetResources(code.Principal.GetResources());
        var application = await applications.FindByClientIdAsync(ClientId) ?? throw new InvalidOperationException();
        var authorization = await authorizations.CreateAsync(identity, principal.GetClaim(Claims.Subject)!,
            (await applications.GetIdAsync(application))!, AuthorizationTypes.Permanent,
            principal.GetScopes());
        principal.SetAuthorizationId(await authorizations.GetIdAsync(authorization));
        return Results.SignIn(principal, authenticationScheme: Protocol);
    }

    private static async Task<IResult> ExchangeAsync(HttpContext http)
    {
        var authentication = await http.AuthenticateAsync(Protocol);
        if (!authentication.Succeeded) return Results.Unauthorized();
        var principal = authentication.Principal!;
        if (!principal.GetResources().SequenceEqual([Resource]))
            return Results.Forbid(authenticationSchemes: [Protocol], properties: new AuthenticationProperties(
                new Dictionary<string, string?> { [OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.InvalidTarget }));
        return Results.SignIn(principal, authenticationScheme: Protocol);
    }

    internal string BrowserToken(string audience = "GameHub.Clients") => new JwtSecurityTokenHandler().WriteToken(
        new JwtSecurityToken("GameHub.Core", audience, [new Claim("sub", "proof-user")],
            expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: new SigningCredentials(browserKey, SecurityAlgorithms.HmacSha256)));

    internal async Task RevokeAsync(string accessToken, bool authorization)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/integration");
        request.Headers.Authorization = new("Bearer", accessToken);
        // This test-only route returns identifiers only after the full validation pipeline runs.
        using var response = await Client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var identifiers = await response.Content.ReadFromJsonAsync<System.Text.Json.Nodes.JsonObject>()
            ?? throw new InvalidOperationException("Revocation setup requires valid access.");
        await using var scope = App.Services.CreateAsyncScope();
        if (authorization)
        {
            var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();
            var entry = await manager.FindByIdAsync(identifiers["authorizationId"]!.GetValue<string>()) ?? throw new InvalidOperationException();
            if (!await manager.TryRevokeAsync(entry)) throw new InvalidOperationException("Authorization revocation failed.");
        }
        else
        {
            var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();
            var entry = await manager.FindByIdAsync(identifiers["tokenId"]!.GetValue<string>()) ?? throw new InvalidOperationException();
            if (!await manager.TryRevokeAsync(entry)) throw new InvalidOperationException("Token revocation failed.");
        }
    }

    internal async Task<bool> AuthorizationIsValidAsync(string id)
    {
        await using var scope = App.Services.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();
        var entry = await manager.FindByIdAsync(id) ?? throw new InvalidOperationException();
        return await manager.HasStatusAsync(entry, Statuses.Valid);
    }

    /// <summary>Disposes the isolated test host, client, and in-memory database.</summary>
    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await App.DisposeAsync();
        await connection.DisposeAsync();
    }
}

internal sealed class ProofDatabase(DbContextOptions<ProofDatabase> options) : DbContext(options);
