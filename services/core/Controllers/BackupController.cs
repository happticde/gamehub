using System.Text.Json;
using System.Text.Json.Serialization;
using GameHub.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GameHub.Core.Controllers;

/// <summary>
/// Provides versioned backup files for the unified GameHub library.
/// </summary>
[ApiController]
[Route("api/backup")]
[Authorize]
public sealed class BackupController : ControllerBase
{
    private static readonly JsonSerializerOptions BackupJsonOptions = CreateJsonOptions();

    private readonly BackupExportService _backupExport;
    private readonly ILogger<BackupController> _logger;

    /// <summary>
    /// Initializes the backup API.
    /// </summary>
    public BackupController(
        BackupExportService backupExport,
        ILogger<BackupController> logger)
    {
        _backupExport = backupExport;
        _logger = logger;
    }

    /// <summary>
    /// GET: api/backup
    /// Downloads a versioned JSON snapshot of the complete unified library.
    /// </summary>
    [HttpGet]
    [Produces("application/json")]
    public async Task<IActionResult> Export(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Creating GameHub library backup");

        var backup = await _backupExport.CreateBackupAsync(cancellationToken);
        var content = JsonSerializer.SerializeToUtf8Bytes(backup, BackupJsonOptions);
        var fileName = $"gamehub-backup-v{backup.FormatVersion}-{backup.ExportedAtUtc:yyyyMMdd-HHmmss}.json";

        _logger.LogInformation(
            "Created GameHub backup with {GameCount} games and {ItemCount} platform items",
            backup.Games.Count,
            backup.Games.Sum(game => game.Items.Count));

        return File(content, "application/json; charset=utf-8", fileName);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
