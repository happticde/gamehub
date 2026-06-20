using System.Security.Claims;
using System.Text.Encodings.Web;
using GameHub.Shared.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace GameHub.Tests;

/// <summary>Security tests for trusted Core-to-Ingest tenant delegation.</summary>
public sealed class TenantDelegationAuthenticationTests
{
    private const string ServiceKey = "internal-core-ingest-key";

    [Fact]
    public async Task CorrectServiceKey_ValidTenant_ProducesDelegatedTenantClaim()
    {
        var tenantId = Guid.NewGuid();
        var context = CreateHttpContext(ServiceKey, tenantId.ToString("D"));
        var handler = await CreateHandler(ServiceKey, context);

        var result = await handler.AuthenticateAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(
            tenantId.ToString("D"),
            result.Principal?.FindFirstValue(GameHubClaimTypes.TenantId));
        Assert.Equal(
            TenantDelegationAuthenticationHandler.SchemeName,
            result.Principal?.FindFirstValue(ClaimTypes.AuthenticationMethod));
    }

    [Fact]
    public async Task WrongServiceKey_ValidTenant_CannotDelegateTenant()
    {
        var context = CreateHttpContext("wrong-key", Guid.NewGuid().ToString("D"));
        var handler = await CreateHandler(ServiceKey, context);

        var result = await handler.AuthenticateAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("Invalid internal service key", result.Failure?.Message);
    }

    [Fact]
    public async Task ServiceKeyNotConfigured_ValidHeaders_CannotDelegateTenant()
    {
        var context = CreateHttpContext(ServiceKey, Guid.NewGuid().ToString("D"));
        var handler = await CreateHandler("", context);

        var result = await handler.AuthenticateAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("is not configured", result.Failure?.Message);
    }

    [Fact]
    public async Task CorrectServiceKey_MissingTenant_CannotAuthenticate()
    {
        var context = CreateHttpContext(ServiceKey, null);
        var handler = await CreateHandler(ServiceKey, context);

        var result = await handler.AuthenticateAsync();

        Assert.False(result.Succeeded);
        Assert.Contains(TenantDelegationAuthenticationHandler.TenantHeaderName, result.Failure?.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task CorrectServiceKey_InvalidTenant_CannotAuthenticate(string tenantHeader)
    {
        var context = CreateHttpContext(ServiceKey, tenantHeader);
        var handler = await CreateHandler(ServiceKey, context);

        var result = await handler.AuthenticateAsync();

        Assert.False(result.Succeeded);
    }

    private static DefaultHttpContext CreateHttpContext(string serviceKey, string? tenantHeader)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[TenantDelegationAuthenticationHandler.ServiceKeyHeaderName] = serviceKey;
        if (tenantHeader is not null)
        {
            context.Request.Headers[TenantDelegationAuthenticationHandler.TenantHeaderName] = tenantHeader;
        }

        return context;
    }

    private static async Task<TenantDelegationAuthenticationHandler> CreateHandler(
        string configuredServiceKey,
        HttpContext context)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:ServiceKey"] = configuredServiceKey
            })
            .Build();
        var options = new OptionsMonitor<AuthenticationSchemeOptions>(
            new OptionsFactory<AuthenticationSchemeOptions>(
                [],
                []),
            [],
            new OptionsCache<AuthenticationSchemeOptions>());
        var handler = new TenantDelegationAuthenticationHandler(
            options,
            NullLoggerFactory.Instance,
            UrlEncoder.Default,
            configuration);
        var scheme = new AuthenticationScheme(
            TenantDelegationAuthenticationHandler.SchemeName,
            null,
            typeof(TenantDelegationAuthenticationHandler));
        await handler.InitializeAsync(scheme, context);
        return handler;
    }
}
