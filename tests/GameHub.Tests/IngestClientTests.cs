using System.Net;
using System.Net.Http.Json;
using GameHub.Core.Data;
using GameHub.Core.Services;
using GameHub.Shared;
using GameHub.Shared.Authentication;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace GameHub.Tests;

/// <summary>Tests that Core authenticates every Ingest request with its active tenant.</summary>
public sealed class IngestClientTests
{
    [Fact]
    public async Task FetchPageAsync_ActiveTenant_SendsInternalKeyAndTenantHeaders()
    {
        var tenantId = Guid.NewGuid();
        var handler = new CapturingHandler();
        var client = CreateClient(handler, tenantId, "dedicated-service-key");

        await client.FetchPageAsync(Platform.Steam);

        Assert.Equal(
            "dedicated-service-key",
            handler.Request?.Headers.GetValues(
                TenantDelegationAuthenticationHandler.ServiceKeyHeaderName).Single());
        Assert.Equal(
            tenantId.ToString("D"),
            handler.Request?.Headers.GetValues(
                TenantDelegationAuthenticationHandler.TenantHeaderName).Single());
        Assert.False(handler.Request!.Headers.Contains(ApiKeyAuthenticationHandler.HeaderName));
    }

    [Fact]
    public async Task FetchPageAsync_MissingInternalKey_FailsBeforeSendingRequest()
    {
        var handler = new CapturingHandler();
        var client = CreateClient(handler, Guid.NewGuid(), "");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.FetchPageAsync(Platform.Steam));

        Assert.Contains("IngestServiceKey", exception.Message);
        Assert.Null(handler.Request);
    }

    [Fact]
    public async Task RetrieveStoredAsync_ActiveTenant_PostsWithInternalDelegationOnly()
    {
        var tenantId = Guid.NewGuid();
        var handler = new CapturingHandler();
        var client = CreateClient(handler, tenantId, "dedicated-service-key");

        var retrieved = await client.RetrieveStoredAsync(Platform.Gog);

        Assert.True(retrieved);
        Assert.Equal(HttpMethod.Post, handler.Request?.Method);
        Assert.Equal("/api/retrieve/gog/stored", handler.Request?.RequestUri?.AbsolutePath);
        Assert.Equal(
            "dedicated-service-key",
            handler.Request?.Headers.GetValues(
                TenantDelegationAuthenticationHandler.ServiceKeyHeaderName).Single());
        Assert.Equal(
            tenantId.ToString("D"),
            handler.Request?.Headers.GetValues(
                TenantDelegationAuthenticationHandler.TenantHeaderName).Single());
        Assert.False(handler.Request!.Headers.Contains(ApiKeyAuthenticationHandler.HeaderName));
    }

    [Fact]
    public async Task RetrieveStoredAsync_MissingCredential_ReturnsFalse()
    {
        var handler = new CapturingHandler(HttpStatusCode.NotFound);
        var client = CreateClient(handler, Guid.NewGuid(), "dedicated-service-key");

        var retrieved = await client.RetrieveStoredAsync(Platform.Psn);

        Assert.False(retrieved);
    }

    private static IngestClient CreateClient(
        CapturingHandler handler,
        Guid tenantId,
        string serviceKey)
    {
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://ingest.test")
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Services:IngestServiceKey"] = serviceKey
            })
            .Build();
        return new IngestClient(http, new FixedTenantContext(tenantId), configuration);
    }

    private sealed class FixedTenantContext(Guid tenantId) : ITenantContext
    {
        public Guid TenantId { get; } = tenantId;
    }

    private sealed class CapturingHandler(HttpStatusCode statusCode = HttpStatusCode.OK)
        : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = JsonContent.Create(new PagedResult<RawGameDto>())
            });
        }
    }
}
