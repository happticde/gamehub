using System.Text.Json;
using GameHub.Ingest.Models;
using GameHub.Shared;

namespace GameHub.Ingest.Services;

/// <summary>Validates and decodes the versioned plaintext inside the encrypted vault.</summary>
internal static class PlatformCredentialPayloadCodec
{
    private const int CurrentSchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void Validate(Platform platform, int schemaVersion, ReadOnlySpan<byte> plaintext)
    {
        if (schemaVersion != CurrentSchemaVersion)
        {
            throw new PlatformCredentialValidationException(
                $"SchemaVersion {schemaVersion} is unsupported for {platform}.");
        }

        switch (platform)
        {
            case Platform.Steam:
                DecodeSteam(plaintext);
                break;
            case Platform.Gog:
                DecodeGog(plaintext);
                break;
            case Platform.Psn:
                DecodePsn(plaintext);
                break;
            case Platform.Epic:
                throw new PlatformCredentialValidationException(
                    "Stored Epic retrieval is not supported yet.");
            default:
                throw new PlatformCredentialValidationException("Platform is unsupported.");
        }
    }

    public static SteamPlatformCredential DecodeSteam(ReadOnlySpan<byte> plaintext)
    {
        var credential = Deserialize<SteamPlatformCredential>(plaintext, Platform.Steam);
        RequireText(credential.ApiKey, "apiKey", 256);
        RequireText(credential.SteamId, "steamId", 32);
        if (!ulong.TryParse(credential.SteamId, out var steamId) || steamId == 0)
        {
            throw new PlatformCredentialValidationException(
                "steamId must be a positive 64-bit Steam identifier.");
        }
        return credential;
    }

    public static GogPlatformCredential DecodeGog(ReadOnlySpan<byte> plaintext)
    {
        var credential = Deserialize<GogPlatformCredential>(plaintext, Platform.Gog);
        RequireText(credential.Cookie, "cookie", 8 * 1024);
        RequireText(credential.User, "user", 256);
        return credential;
    }

    public static PsnPlatformCredential DecodePsn(ReadOnlySpan<byte> plaintext)
    {
        var credential = Deserialize<PsnPlatformCredential>(plaintext, Platform.Psn);
        if (credential.Cookies is null || credential.Cookies.Count == 0)
        {
            throw new PlatformCredentialValidationException(
                "cookies must contain at least one entry.");
        }
        if (credential.Cookies.Count > 64)
        {
            throw new PlatformCredentialValidationException(
                "cookies must not contain more than 64 entries.");
        }

        foreach (var (name, value) in credential.Cookies)
        {
            RequireText(name, "cookie name", 256);
            RequireText(value, $"cookie '{name}'", 8 * 1024);
        }
        return credential;
    }

    private static T Deserialize<T>(ReadOnlySpan<byte> plaintext, Platform platform)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(plaintext, JsonOptions)
                ?? throw new PlatformCredentialValidationException(
                    $"Credential payload for {platform} is invalid.");
        }
        catch (JsonException ex)
        {
            throw new PlatformCredentialValidationException(
                $"Credential payload for {platform} does not match schema version 1.",
                ex);
        }
    }

    private static void RequireText(string? value, string field, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new PlatformCredentialValidationException($"{field} is required.");
        }
        if (value.Length > maximumLength)
        {
            throw new PlatformCredentialValidationException(
                $"{field} must not exceed {maximumLength} characters.");
        }
    }
}
