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

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new PagedResult<RawGameDto>())
            });
        }
    }
}
