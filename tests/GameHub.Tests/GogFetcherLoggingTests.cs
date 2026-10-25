using System.Net;
using GameHub.Ingest.Data;
using GameHub.Ingest.Services;
using GameHub.Tests.Helpers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace GameHub.Tests;

public sealed class GogFetcherLoggingTests
{
    [Fact]
    public async Task FetchGamesAsync_UserNameAndRequestUrl_AreNotLogged()
    {
        const string username = "gog-user-1f90d2\r\nCRITICAL forged log entry";
        const string cookie = "gog-cookie-secret";
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<IngestContext>().UseSqlite(connection).Options;
        await using var db = new IngestContext(options, new DefaultIngestTenantContext());
        await db.Database.EnsureCreatedAsync();
        using var services = new ServiceCollection()
            .AddScoped(_ => new IngestContext(options, new DefaultIngestTenantContext()))
            .BuildServiceProvider();
        var logger = new RecordingLogger<GogFetcher>();
        string? receivedCookie = null;
        Uri? requestedUri = null;
        var fetcher = new GogFetcher(logger, services.GetRequiredService<IServiceScopeFactory>(), value =>
        {
            receivedCookie = value;
            return new StubHandler(request =>
            {
                requestedUri = request.RequestUri;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                        {"pages":1,"_embedded":{"items":[{"game":{"id":42,"title":"Example"}}]}}
                        """)
                };
            });
        });

        var games = await fetcher.FetchGamesAsync(cookie, username);

        Assert.Equal(cookie, receivedCookie);
        Assert.Equal("www.gog.com", requestedUri?.Host);
        Assert.Single(games);
        Assert.Contains(logger.Records, entry =>
            entry.Level == LogLevel.Debug && entry.Message.Contains("fetching page 1", StringComparison.Ordinal));
        Assert.All(logger.Records, entry =>
        {
            Assert.DoesNotContain(username, entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("forged log entry", entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("www.gog.com", entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(cookie, entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(entry.Properties, property =>
                property.Value?.ToString()?.Contains("forged log entry", StringComparison.Ordinal) == true);
        });
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
