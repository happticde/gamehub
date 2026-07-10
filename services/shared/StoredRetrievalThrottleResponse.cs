namespace GameHub.Shared;

/// <summary>Secret-free rate-limit detail returned only by the internal stored-retrieval route.</summary>
public sealed record StoredRetrievalThrottleResponse(DateTimeOffset? RetryAfterUtc);
