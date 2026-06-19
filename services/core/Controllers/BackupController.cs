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
    private readonly BackupExportService _backupExport;
    private readonly BackupJsonSerializer _backupJson;
    private readonly BackupCsvSerializer _backupCsv;
    private readonly ILogger<BackupController> _logger;

    /// <summary>
    /// Initializes the backup API.
    /// </summary>
    public BackupController(
        BackupExportService backupExport,
        BackupJsonSerializer backupJson,
        BackupCsvSerializer backupCsv,
        ILogger<BackupController> logger)
    {
        _backupExport = backupExport;
        _backupJson = backupJson;
        _backupCsv = backupCsv;
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
        var content = _backupJson.Serialize(backup);
        var fileName = $"gamehub-backup-v{backup.FormatVersion}-{backup.ExportedAtUtc:yyyyMMdd-HHmmss}.json";

        _logger.LogInformation(
            "Created GameHub backup with {GameCount} games and {ItemCount} platform items",
            backup.Games.Count,
            backup.Games.Sum(game => game.Items.Count));

        return File(content, "application/json; charset=utf-8", fileName);
    }

    /// <summary>
    /// GET: api/backup/csv
    /// Downloads a flat, spreadsheet-friendly view of the unified library.
    /// </summary>
    [HttpGet("csv")]
    [Produces("text/csv")]
    public async Task<IActionResult> ExportCsv(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Creating GameHub library CSV export");

        var backup = await _backupExport.CreateBackupAsync(cancellationToken);
        var content = _backupCsv.Serialize(backup);
        var fileName = $"gamehub-library-{backup.ExportedAtUtc:yyyyMMdd-HHmmss}.csv";

        _logger.LogInformation(
            "Created GameHub CSV export with {GameCount} games",
            backup.Games.Count);

        return File(content, "text/csv; charset=utf-8", fileName);
    }
}
