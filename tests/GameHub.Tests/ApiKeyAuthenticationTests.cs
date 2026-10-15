using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using GameHub.Shared.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using Microsoft.Extensions.Hosting;
using Moq;

namespace GameHub.Tests;

/// <summary>
/// Tests for the ApiKeyAuthenticationHandler — the core security component.
///
/// Covers all four scenarios:
///   1. No key configured → fail closed unless anonymous development is explicitly enabled
///   2. Key configured, no header → 401
///   3. Key configured, wrong key → 401
///   4. Key configured, correct key → authenticated
/// </summary>
public class ApiKeyAuthenticationTests
{
    private const string TestApiKey = "test-secret-key-123";

    /// <summary>
    /// Creates an ApiKeyAuthenticationHandler wired up with the given config
    /// and HTTP context. Uses the real ASP.NET authentication pipeline.
    /// </summary>
    private async Task<ApiKeyAuthenticationHandler> CreateHandler(
        Dictionary<string, string?> configValues,
        HttpContext httpContext,
        string environmentName = "Development")
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues)
            .Build();

        var options = new OptionsMonitor<AuthenticationSchemeOptions>(
            new OptionsFactory<AuthenticationSchemeOptions>(
                Enumerable.Empty<IConfigureOptions<AuthenticationSchemeOptions>>(),
                Enumerable.Empty<IPostConfigureOptions<AuthenticationSchemeOptions>>()),
            Enumerable.Empty<IOptionsChangeTokenSource<AuthenticationSchemeOptions>>(),
            new OptionsCache<AuthenticationSchemeOptions>());

        var loggerFactory = NullLoggerFactory.Instance;

        var handler = new ApiKeyAuthenticationHandler(
            options, loggerFactory, UrlEncoder.Default, configuration,
            Mock.Of<IHostEnvironment>(environment => environment.EnvironmentName == environmentName));

        var scheme = new AuthenticationScheme(
            ApiKeyAuthenticationHandler.SchemeName,
            displayName: null,
            typeof(ApiKeyAuthenticationHandler));

        await handler.InitializeAsync(scheme, httpContext);
        return handler;
    }

    private HttpContext CreateHttpContext(string? apiKeyHeader = null)
    {
        var context = new DefaultHttpContext();
        if (apiKeyHeader != null)
        {
            context.Request.Headers["X-Api-Key"] = apiKeyHeader;
        }
        return context;
    }

    // =========================================================================
    // Scenario 1: Missing API keys fail closed; anonymous development is an explicit opt-in.
    // =========================================================================

    [Fact]
    public async Task NoKeyConfigured_FailsClosed()
    {
        var context = CreateHttpContext();
        var handler = await CreateHandler(
            new Dictionary<string, string?> { { "Authentication:ApiKey", "" } },
            context);

        var result = await handler.AuthenticateAsync();

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task NullKeyConfigured_FailsClosed()
    {
        var context = CreateHttpContext();
        var handler = await CreateHandler(
            new Dictionary<string, string?>(), // No key at all
            context);

        var result = await handler.AuthenticateAsync();

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task ExplicitAnonymousDevelopment_NoBrowserAuthentication_AllowsDefaultTenant()
    {
        var handler = await CreateHandler(new Dictionary<string, string?>
        {
            [AuthenticationSafety.AnonymousDevelopmentKey] = "true"
        }, CreateHttpContext());
        var result = await handler.AuthenticateAsync();
        Assert.True(result.Succeeded);
        Assert.Equal(GameHubTenantDefaults.DefaultTenantId.ToString("D"),
            result.Principal?.FindFirst(GameHubClaimTypes.TenantId)?.Value);
    }

    [Fact]
    public async Task ProductionHandler_AnonymousFlagCannotDisableAuthentication()
    {
        var handler = await CreateHandler(new Dictionary<string, string?>
        {
            [AuthenticationSafety.AnonymousDevelopmentKey] = "true"
        }, CreateHttpContext(), "Production");
        Assert.False((await handler.AuthenticateAsync()).Succeeded);
    }

    [Theory]
    [InlineData("Authentication:Jwt:SigningKey", "configured-signing-key")]
    [InlineData("Authentication:Frontend:Enabled", "true")]
    [InlineData("Authentication:Oidc:Enabled", "true")]
    [InlineData("MultiUser:Enabled", "true")]
    public async Task ExplicitAnonymousDevelopment_BrowserAuthenticationConfigured_CannotBypassLogin(string key, string value)
    {
        var handler = await CreateHandler(new Dictionary<string, string?>
        {
            [AuthenticationSafety.AnonymousDevelopmentKey] = "true",
            [key] = value
        }, CreateHttpContext());
        Assert.False((await handler.AuthenticateAsync()).Succeeded);
    }

    // =========================================================================
    // Scenario 2: Key configured, no header → rejected
    // =========================================================================

    [Fact]
    public async Task KeyConfigured_NoHeader_Fails()
    {
        var context = CreateHttpContext(); // No X-Api-Key header
        var handler = await CreateHandler(
            new Dictionary<string, string?> { { "Authentication:ApiKey", TestApiKey } },
            context);

        var result = await handler.AuthenticateAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("Missing", result.Failure?.Message);
    }

    // =========================================================================
    // Scenario 3: Key configured, wrong key → rejected
    // =========================================================================

    [Fact]
    public async Task KeyConfigured_WrongKey_Fails()
    {
        var context = CreateHttpContext("wrong-key");
        var handler = await CreateHandler(
            new Dictionary<string, string?> { { "Authentication:ApiKey", TestApiKey } },
            context);

        var result = await handler.AuthenticateAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("Invalid", result.Failure?.Message);
    }

    [Fact]
    public async Task KeyConfigured_MultipleHeaderValues_FailsClosed()
    {
        var context = CreateHttpContext();
        context.Request.Headers[ApiKeyAuthenticationHandler.HeaderName] =
            new Microsoft.Extensions.Primitives.StringValues([TestApiKey, TestApiKey]);
        var handler = await CreateHandler(
            new Dictionary<string, string?> { { "Authentication:ApiKey", TestApiKey } },
            context);

        var result = await handler.AuthenticateAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("Missing", result.Failure?.Message);
    }

    // =========================================================================
    // Scenario 4: Key configured, correct key → authenticated
    // =========================================================================

    [Fact]
    public async Task KeyConfigured_CorrectKey_Succeeds()
    {
        var context = CreateHttpContext(TestApiKey);
        var handler = await CreateHandler(
            new Dictionary<string, string?> { { "Authentication:ApiKey", TestApiKey } },
            context);

        var result = await handler.AuthenticateAsync();

        Assert.True(result.Succeeded);
        Assert.Equal("ApiKeyUser", result.Principal?.Identity?.Name);
    }

    [Fact]
    public async Task KeyConfigured_CorrectKey_HasAuthMethodClaim()
    {
        var context = CreateHttpContext(TestApiKey);
        var handler = await CreateHandler(
            new Dictionary<string, string?> { { "Authentication:ApiKey", TestApiKey } },
            context);

        var result = await handler.AuthenticateAsync();

        Assert.True(result.Succeeded);
        var authMethodClaim = result.Principal?.FindFirst(ClaimTypes.AuthenticationMethod);
        Assert.NotNull(authMethodClaim);
        Assert.Equal("ApiKey", authMethodClaim.Value);
    }

    [Fact]
    public async Task KeyConfigured_CorrectKey_HasDefaultTenantClaim()
    {
        var context = CreateHttpContext(TestApiKey);
        var handler = await CreateHandler(
            new Dictionary<string, string?> { { "Authentication:ApiKey", TestApiKey } },
            context);

        var result = await handler.AuthenticateAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(
            GameHubTenantDefaults.DefaultTenantId.ToString("D"),
            result.Principal?.FindFirst(GameHubClaimTypes.TenantId)?.Value);
    }

    [Fact]
    public async Task TenantConfigured_CorrectKey_HasConfiguredTenantClaim()
    {
        var tenantId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var context = CreateHttpContext(TestApiKey);
        var handler = await CreateHandler(
            new Dictionary<string, string?>
            {
                { "Authentication:ApiKey", TestApiKey },
                { "Authentication:TenantId", tenantId.ToString("D") }
            },
            context);

        var result = await handler.AuthenticateAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(
            tenantId.ToString("D"),
            result.Principal?.FindFirst(GameHubClaimTypes.TenantId)?.Value);
    }

    [Fact]
    public async Task CorrectApiKey_TenantDelegationHeader_IsRejected()
    {
        var context = CreateHttpContext(TestApiKey);
        context.Request.Headers[TenantDelegationAuthenticationHandler.TenantHeaderName] =
            Guid.NewGuid().ToString("D");
        var handler = await CreateHandler(
            new Dictionary<string, string?> { { "Authentication:ApiKey", TestApiKey } },
            context);

        var result = await handler.AuthenticateAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("requires internal service authentication", result.Failure?.Message);
    }

    [Fact]
    public async Task InvalidTenantConfigured_AuthenticationFails()
    {
        var context = CreateHttpContext(TestApiKey);
        var handler = await CreateHandler(
            new Dictionary<string, string?>
            {
                { "Authentication:ApiKey", TestApiKey },
                { "Authentication:TenantId", "not-a-guid" }
            },
            context);

        var result = await handler.AuthenticateAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("non-empty GUID", result.Failure?.Message);
    }
}
