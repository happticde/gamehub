using System.Text.Json;
using GameHub.Shared;

namespace GameHub.Ingest.Models;

/// <summary>Versioned platform credential payload accepted by the internal vault API.</summary>
public sealed class UpsertPlatformCredentialRequest
{
    public int SchemaVersion { get; init; } = 1;
    public JsonElement Credentials { get; init; }
}

/// <summary>Secret-free configuration state for one platform.</summary>
public sealed record PlatformCredentialStatusResponse(
    Platform Platform,
    bool IsConfigured,
    int? SchemaVersion,
    DateTime? UpdatedAtUtc);

/// <summary>Uniform error response from the internal credential vault.</summary>
public sealed record PlatformCredentialErrorResponse(string Message);
