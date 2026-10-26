using System.Text.Json;
using GameHub.Ingest.Models;
using GameHub.Shared;

namespace GameHub.Ingest.Services;

/// <summary>Validates and decodes the versioned plaintext inside the encrypted vault.</summary>
internal static class PlatformCredentialPayloadCodec
{
    private const int CurrentSchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void ValidateSchemaVersion(Platform platform, int schemaVersion)
    {
        if (schemaVersion != CurrentSchemaVersion)
        {
            throw new PlatformCredentialValidationException(
                $"SchemaVersion {schemaVersion} is unsupported for {platform}.");
        }
    }

    public static void Validate(Platform platform, int schemaVersion, ReadOnlySpan<byte> plaintext)
    {
        ValidateSchemaVersion(platform, schemaVersion);
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
                DecodeEpic(plaintext);
                break;
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

    public static EpicPlatformCredential DecodeEpic(ReadOnlySpan<byte> plaintext)
    {
        var credential = Deserialize<EpicPlatformCredential>(plaintext, Platform.Epic);
        RequireText(credential.RefreshToken, "refreshToken", 8 * 1024);
        RequireOptionalText(credential.AccountId, "accountId", 128);
        RequireOptionalText(credential.DisplayName, "displayName", 256);
        return credential;
    }

    public static string? ReadEpicAuthorizationCode(JsonElement credentials)
    {
        if (!credentials.TryGetProperty("authorizationCode", out var codeProperty))
        {
            return null;
        }
        if (codeProperty.ValueKind is not JsonValueKind.String and not JsonValueKind.Null)
        {
            throw new PlatformCredentialValidationException("authorizationCode must be a string.");
        }

        var code = codeProperty.GetString();
        RequireText(code, "authorizationCode", 2048);
        return code;
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

    private static void RequireOptionalText(string? value, string field, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }
        if (value.Length > maximumLength)
        {
            throw new PlatformCredentialValidationException(
                $"{field} must not exceed {maximumLength} characters.");
        }
    }
}
