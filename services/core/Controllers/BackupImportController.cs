using GameHub.Core.Models.Backup;
using GameHub.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GameHub.Core.Controllers;

/// <summary>
/// Validates and restores uploaded GameHub backup files.
/// </summary>
[ApiController]
[Route("api/backup")]
[Authorize]
public sealed class BackupImportController : ControllerBase
{
    /// <summary>Maximum accepted JSON backup file size: 25 MiB.</summary>
    public const long MaxBackupFileSizeBytes = 25L * 1024 * 1024;

    private const long MaxRequestSizeBytes = MaxBackupFileSizeBytes + (1024 * 1024);

    private readonly BackupImportService _backupImport;
    private readonly ILogger<BackupImportController> _logger;

    /// <summary>
    /// Initializes the backup import API.
    /// </summary>
    public BackupImportController(
        BackupImportService backupImport,
        ILogger<BackupImportController> logger)
    {
        _backupImport = backupImport;
        _logger = logger;
    }

    /// <summary>
    /// POST: api/backup/validate
    /// Parses a backup upload and returns a non-destructive content preview.
    /// </summary>
    [HttpPost("validate")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxRequestSizeBytes)]
    [ProducesResponseType<BackupValidationResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<BackupValidationResult>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<BackupValidationResult>(StatusCodes.Status413PayloadTooLarge)]
    public async Task<IActionResult> Validate(
        [FromForm] IFormFile? file,
        CancellationToken cancellationToken)
    {
        var uploadError = ValidateUpload(file);
        if (uploadError is not null)
        {
            return file is { Length: > MaxBackupFileSizeBytes }
                ? StatusCode(StatusCodes.Status413PayloadTooLarge, uploadError)
                : BadRequest(uploadError);
        }

        await using var stream = file!.OpenReadStream();
        var (_, validation) = await _backupImport.ReadAndValidateAsync(stream, cancellationToken);

        if (!validation.IsValid)
        {
            _logger.LogWarning(
                "Rejected invalid GameHub backup with {ErrorCount} validation errors",
                validation.Errors.Count);
            return BadRequest(validation);
        }

        _logger.LogInformation(
            "Validated GameHub backup with {GameCount} games and {ItemCount} platform items",
            validation.Summary.GameCount,
            validation.Summary.PlatformItemCount);
        return Ok(validation);
    }

    /// <summary>
    /// POST: api/backup/restore
    /// Restores a valid backup transactionally into an empty unified library.
    /// </summary>
    [HttpPost("restore")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxRequestSizeBytes)]
    [ProducesResponseType<BackupRestoreResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<BackupValidationResult>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<BackupRestoreResult>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<BackupValidationResult>(StatusCodes.Status413PayloadTooLarge)]
    public async Task<IActionResult> Restore(
        [FromForm] IFormFile? file,
        CancellationToken cancellationToken)
    {
        var uploadError = ValidateUpload(file);
        if (uploadError is not null)
        {
            return file is { Length: > MaxBackupFileSizeBytes }
                ? StatusCode(StatusCodes.Status413PayloadTooLarge, uploadError)
                : BadRequest(uploadError);
        }

        await using var stream = file!.OpenReadStream();
        var (backup, validation) = await _backupImport.ReadAndValidateAsync(stream, cancellationToken);

        if (!validation.IsValid || backup is null)
        {
            _logger.LogWarning(
                "Rejected restore from invalid GameHub backup with {ErrorCount} validation errors",
                validation.Errors.Count);
            return BadRequest(validation);
        }

        var result = await _backupImport.RestoreAsync(backup, cancellationToken);
        if (!result.Restored)
        {
            _logger.LogWarning("Rejected GameHub restore because the target library is not empty");
            return Conflict(result);
        }

        _logger.LogInformation(
            "Restored GameHub backup with {GameCount} games and {ItemCount} platform items",
            result.Summary.GameCount,
            result.Summary.PlatformItemCount);
        return Ok(result);
    }

    private static BackupValidationResult? ValidateUpload(IFormFile? file)
    {
        if (file is null || file.Length == 0)
        {
            return UploadError("missing_file", "A non-empty JSON backup file is required.");
        }

        if (file.Length > MaxBackupFileSizeBytes)
        {
            return UploadError(
                "file_too_large",
                $"Backup files may not exceed {MaxBackupFileSizeBytes / (1024 * 1024)} MiB.");
        }

        return null;
    }

    private static BackupValidationResult UploadError(string code, string message) =>
        new(
            false,
            null,
            null,
            new BackupSummary(0, 0, 0, 0),
            [new BackupValidationError(code, message)]);
}
