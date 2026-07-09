using System.Net;
using System.Net.Http.Headers;
using GameHub.Ingest.Services;
using Xunit;

namespace GameHub.Tests;

public sealed class ProviderRequestExceptionTests
{
    [Fact]
    public void FromResponse_RateLimitWithDelta_ExposesRetryAfterUtcAndSafeAuditMessage()
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(5));
        var now = new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);

        var exception = ProviderRequestException.FromResponse(response, new FixedTimeProvider(now));

        Assert.Equal(ProviderRequestFailure.RateLimited, exception.Failure);
        Assert.Equal(now.AddMinutes(5), exception.RetryAfterUtc);
        Assert.Equal("RateLimited", exception.AuditStatus);
        Assert.Equal(
            "Provider rate limit exceeded. Retry after 2030-01-02T03:09:05.0000000+00:00.",
            exception.GetSafeAuditMessage());
    }

    [Fact]
    public void FromResponse_RateLimitWithDate_NormalizesRetryAfterToUtc()
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(
            new DateTimeOffset(2030, 1, 2, 4, 4, 5, TimeSpan.FromHours(1)));

        var exception = ProviderRequestException.FromResponse(response);

        Assert.Equal(new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero), exception.RetryAfterUtc);
    }

    [Fact]
    public void FromResponse_UpstreamFailure_UsesTemporaryFailureAuditStateWithoutRetryTime()
    {
        var exception = ProviderRequestException.FromResponse(
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        Assert.Equal(ProviderRequestFailure.UpstreamUnavailable, exception.Failure);
        Assert.Null(exception.RetryAfterUtc);
        Assert.Equal("UpstreamUnavailable", exception.AuditStatus);
        Assert.Equal("Provider is temporarily unavailable.", exception.GetSafeAuditMessage());
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
