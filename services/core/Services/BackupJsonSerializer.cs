using System.Text.Json;
using System.Text.Json.Serialization;
using GameHub.Core.Models.Backup;

namespace GameHub.Core.Services;

/// <summary>
/// Serializes and deserializes the versioned GameHub backup contract.
/// </summary>
public sealed class BackupJsonSerializer
{
    private readonly JsonSerializerOptions _options = CreateJsonOptions();

    /// <summary>
    /// Serializes a backup using camel-case properties, indented JSON, and string enums.
    /// </summary>
    public byte[] Serialize(GameHubBackupV1 backup) =>
        JsonSerializer.SerializeToUtf8Bytes(backup, _options);

    /// <summary>
    /// Deserializes a supported backup document. JSON contract errors are reported as exceptions
    /// so callers can convert them into structured validation responses.
    /// </summary>
    public async Task<GameHubBackupV1?> DeserializeAsync(
        Stream stream,
        CancellationToken cancellationToken = default) =>
        await JsonSerializer.DeserializeAsync<GameHubBackupV1>(
            stream,
            _options,
            cancellationToken);

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        options.Converters.Add(new JsonStringEnumConverter(
            namingPolicy: null,
            allowIntegerValues: false));
        return options;
    }
}
