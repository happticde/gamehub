using System.Text.Json;
using GameHub.Shared;

namespace GameHub.Core.Models.PlatformCredentials;

/// <summary>Versioned credential payload forwarded to Ingest's encrypted tenant vault.</summary>
public sealed class UpsertPlatformCredentialRequest
{
    public int SchemaVersion { get; init; } = 1;
    public JsonElement Credentials { get; init; }
}

/// <summary>Secret-free configuration state for one tenant platform credential.</summary>
public sealed record PlatformCredentialStatusResponse(
    Platform Platform,
    bool IsConfigured,
    int? SchemaVersion,
    DateTime? UpdatedAtUtc);

/// <summary>Credential state plus Core's server-derived sync activation decision.</summary>
public sealed record PlatformCredentialManagementResponse(
    bool PlatformSyncEnabled,
    IReadOnlyList<PlatformCredentialStatusResponse> Credentials);

/// <summary>Result of an immediate stored-credential retrieval request.</summary>
public sealed record PlatformRetrievalResponse(Platform Platform, string Message);

/// <summary>Tenant-scoped audit entry returned by Ingest after platform retrieval.</summary>
public sealed record PlatformRetrievalAuditLogResponse(
    int Id,
    DateTime Timestamp,
    string Platform,
    string Status,
    int ItemsProcessed,
    string? ErrorMessage);

/// <summary>Structured, secret-free error returned by the Core management API.</summary>
public sealed record PlatformCredentialErrorResponse(string Code, string Message);
