using System.Net;
using GameHub.Ingest.Data;
using GameHub.Ingest.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GameHub.Tests;

/// <summary>Response ownership tests for Steam retrieval and its returned JSON elements.</summary>
public sealed class SteamFetcherTests
{
    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task FetchGamesAsync_ResponseCompleted_DisposesContentAndPreservesReturnedGames(HttpStatusCode status)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<IngestContext>().UseSqlite(connection).Options;
        await using var db = new IngestContext(options, new DefaultIngestTenantContext());
        await db.Database.EnsureCreatedAsync();
        using var services = new ServiceCollection()
            .AddScoped(_ => new IngestContext(options, new DefaultIngestTenantContext()))
            .BuildServiceProvider();
        var content = new TrackedContent("""
            {"response":{"games":[{"appid":42,"name":"Example","playtime_forever":12}]}}
            """);
        var handler = new StubHandler(status, content);
        using var http = new HttpClient(handler);
        var fetcher = new SteamFetcher(http, NullLogger<SteamFetcher>.Instance,
            services.GetRequiredService<IServiceScopeFactory>());

        if (status == HttpStatusCode.OK)
        {
            var games = await fetcher.FetchGamesAsync("test-key", "test-user");
            Assert.True(content.IsDisposed);
            var game = Assert.Single(games);
            Assert.Equal(42, game.GetProperty("appid").GetInt32());
            Assert.Equal("Example", game.GetProperty("name").GetString());
            Assert.Equal("test-key", handler.ApiKeyHeader);
            Assert.DoesNotContain("test-key", handler.RequestUriQuery);
        }
        else
        {
            await Assert.ThrowsAnyAsync<HttpRequestException>(() => fetcher.FetchGamesAsync("test-key", "test-user"));
            Assert.True(content.IsDisposed);
        }

        Assert.Equal(1, await db.IngestAuditLogs.CountAsync());
    }

    private sealed class StubHandler(HttpStatusCode status, HttpContent content) : HttpMessageHandler
    {
        public string? ApiKeyHeader { get; private set; }
        public string RequestUriQuery { get; private set; } = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ApiKeyHeader = request.Headers.GetValues("x-webapi-key").SingleOrDefault();
            RequestUriQuery = request.RequestUri?.Query ?? string.Empty;
            return Task.FromResult(new HttpResponseMessage(status) { Content = content });
        }
    }

    private sealed class TrackedContent(string body) : StringContent(body)
    {
        public bool IsDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }
}
