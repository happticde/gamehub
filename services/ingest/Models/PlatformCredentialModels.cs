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

/// <summary>Schema v1 for Steam retrieval credentials.</summary>
public sealed record SteamPlatformCredential(string ApiKey, string SteamId);

/// <summary>Schema v1 for GOG retrieval credentials.</summary>
public sealed record GogPlatformCredential(string Cookie, string User);

/// <summary>Schema v1 for PlayStation retrieval credentials.</summary>
public sealed record PsnPlatformCredential(Dictionary<string, string> Cookies);

/// <summary>Schema v1 for Epic Games retrieval credentials stored in the vault.</summary>
public sealed record EpicPlatformCredential(
    string RefreshToken,
    string? AccountId = null,
    string? DisplayName = null);
