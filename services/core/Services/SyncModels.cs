using GameHub.Shared;

namespace GameHub.Core.Services;

/// <summary>Controls whether a sync may contact external platform providers.</summary>
public enum SyncExecutionMode
{
    Cached,
    RefreshIfStale,
    ForceRefresh
}

/// <summary>One platform's provider-refresh and cached-export result.</summary>
public sealed record PlatformSyncResult(
    Platform Platform,
    string RefreshStatus,
    int ItemsProcessed,
    string? Detail = null);

/// <summary>Complete result returned by manual and scheduled sync orchestration.</summary>
public sealed record LibrarySyncResult(
    string Mode,
    int Processed,
    IReadOnlyList<PlatformSyncResult> Platforms,
    string Message);
