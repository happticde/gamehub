using System.Net;
using System.Net.Http.Headers;

namespace GameHub.Ingest.Services;

/// <summary>Safe classification for failed provider HTTP requests without retaining response content.</summary>
public sealed class ProviderRequestException : HttpRequestException
{
    private ProviderRequestException(
        ProviderRequestFailure failure,
        HttpStatusCode statusCode,
        DateTimeOffset? retryAfterUtc)
        : base("Provider request failed.", null, statusCode)
    {
        Failure = failure;
        RetryAfterUtc = retryAfterUtc;
    }

    /// <summary>The safe request-failure category shared by platform fetchers.</summary>
    public ProviderRequestFailure Failure { get; }

    /// <summary>Provider-supplied retry time when a rate-limit response includes one.</summary>
    public DateTimeOffset? RetryAfterUtc { get; }

    /// <summary>Builds a safe failure signal from a non-success provider response.</summary>
    public static ProviderRequestException FromResponse(
        HttpResponseMessage response,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.IsSuccessStatusCode)
        {
            throw new ArgumentException("Provider response must be unsuccessful.", nameof(response));
        }

        var failure = response.StatusCode switch
        {
            HttpStatusCode.TooManyRequests => ProviderRequestFailure.RateLimited,
            _ when (int)response.StatusCode >= (int)HttpStatusCode.InternalServerError =>
                ProviderRequestFailure.UpstreamUnavailable,
            _ => ProviderRequestFailure.RequestFailed
        };

        return new ProviderRequestException(
            failure,
            response.StatusCode,
            failure == ProviderRequestFailure.RateLimited
                ? ReadRetryAfterUtc(response.Headers.RetryAfter, timeProvider ?? TimeProvider.System)
                : null);
    }

    /// <summary>Returns the existing audit status matching this safe failure category.</summary>
    public string AuditStatus => Failure switch
    {
        ProviderRequestFailure.RateLimited => "RateLimited",
        ProviderRequestFailure.UpstreamUnavailable => "UpstreamUnavailable",
        _ => "Error"
    };

    /// <summary>Returns a secret-free audit message suitable for tenant-scoped history.</summary>
    public string GetSafeAuditMessage() => Failure switch
    {
        ProviderRequestFailure.RateLimited when RetryAfterUtc.HasValue =>
            $"Provider rate limit exceeded. Retry after {RetryAfterUtc.Value:O}.",
        ProviderRequestFailure.RateLimited => "Provider rate limit exceeded.",
        ProviderRequestFailure.UpstreamUnavailable => "Provider is temporarily unavailable.",
        _ => $"Provider request failed with HTTP {(int)(StatusCode ?? 0)}."
    };

    private static DateTimeOffset? ReadRetryAfterUtc(
        RetryConditionHeaderValue? retryAfter,
        TimeProvider timeProvider)
    {
        if (retryAfter?.Date is { } date)
        {
            return date.ToUniversalTime();
        }

        return retryAfter?.Delta is { } delay && delay >= TimeSpan.Zero
            ? timeProvider.GetUtcNow().Add(delay)
            : null;
    }
}

/// <summary>Safe provider transport categories shared by platform fetchers.</summary>
public enum ProviderRequestFailure
{
    RateLimited,
    UpstreamUnavailable,
    RequestFailed
}
