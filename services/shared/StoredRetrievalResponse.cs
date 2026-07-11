namespace GameHub.Shared;

/// <summary>
/// Secret-free result of a stored provider retrieval. A partial retrieval may still update raw
/// data, but callers must not treat it as a complete freshness checkpoint.
/// </summary>
public sealed record StoredRetrievalResponse(bool IsComplete);
