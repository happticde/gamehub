using System.Text;
using GameHub.Core.Controllers;
using GameHub.Core.Data;
using GameHub.Core.Models.Backup;
using GameHub.Core.Services;
using GameHub.Shared;
using GameHub.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GameHub.Tests;

/// <summary>
/// Integration tests for backup validation and transactional restore.
/// </summary>
public sealed class BackupImportTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 6, 19, 14, 0, 0, TimeSpan.Zero);

    private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
    private readonly CoreContext _db;
    private readonly BackupJsonSerializer _serializer = new();
    private readonly BackupImportService _service;

    public BackupImportTests()
    {
        var (db, connection) = DatabaseFixture.CreateCoreContext();
        _db = db;
        _connection = connection;
        _service = new BackupImportService(_db, _serializer, new FixedTimeProvider(FixedNow));
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task ReadAndValidate_ValidBackup_ReturnsContentSummary()
    {
        var backup = CreateBackup(
            CreateGame("doom", "DOOM", "379720", ["Action", "FPS"]),
            CreateGame("quake", "Quake", "2310", ["FPS"]));
        await using var stream = new MemoryStream(_serializer.Serialize(backup));

        var (parsed, validation) = await _service.ReadAndValidateAsync(stream);

        Assert.NotNull(parsed);
        Assert.True(validation.IsValid);
        Assert.Empty(validation.Errors);
        Assert.Equal(2, validation.Summary.GameCount);
        Assert.Equal(2, validation.Summary.PlatformItemCount);
        Assert.Equal(2, validation.Summary.ManualLinkCount);
        Assert.Equal(2, validation.Summary.TagCount);
    }

    [Fact]
    public async Task ReadAndValidate_MalformedJson_ReturnsStructuredError()
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("{ not-valid-json"));

        var (parsed, validation) = await _service.ReadAndValidateAsync(stream);

        Assert.Null(parsed);
        Assert.False(validation.IsValid);
        var error = Assert.Single(validation.Errors);
        Assert.Equal("invalid_json", error.Code);
    }

    [Fact]
    public async Task ReadAndValidate_UnsupportedVersion_ReturnsVersionError()
    {
        var backup = CreateBackup(CreateGame("doom", "DOOM", "379720", ["Action"])) with
        {
            FormatVersion = BackupExportService.CurrentFormatVersion + 1
        };
        await using var stream = new MemoryStream(_serializer.Serialize(backup));

        var (parsed, validation) = await _service.ReadAndValidateAsync(stream);

        Assert.NotNull(parsed);
        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, error => error.Code == "unsupported_version");
    }

    [Fact]
    public void Validate_ConflictingRelationships_ReturnsAllRelevantErrors()
    {
        var firstGame = CreateGame("duplicate", "First", "10", ["RPG", "rpg"]);
        firstGame = firstGame with
        {
            PersonalTracking = firstGame.PersonalTracking with { UserRating = 6 },
            Metadata = firstGame.Metadata with { Rating = 101 },
            Items =
            [
                new BackupLibraryItemV1(Platform.Steam, "10", FixedNow.UtcDateTime),
                new BackupLibraryItemV1(Platform.Steam, "11", FixedNow.UtcDateTime)
            ],
            PhysicalCopies =
            [
                new BackupPhysicalGameCopyV2(PhysicalGamePlatform.PS5),
                new BackupPhysicalGameCopyV2(PhysicalGamePlatform.PS5)
            ]
        };
        var secondGame = CreateGame("duplicate", "Second", "10", ["Action"]);
        var backup = CreateBackup(firstGame, secondGame);

        var validation = _service.Validate(backup);
        var codes = validation.Errors.Select(error => error.Code).ToHashSet();

        Assert.False(validation.IsValid);
        Assert.Contains("invalid_user_rating", codes);
        Assert.Contains("invalid_metadata_rating", codes);
        Assert.Contains("duplicate_tag", codes);
        Assert.Contains("duplicate_game_platform", codes);
        Assert.Contains("duplicate_slug", codes);
        Assert.Contains("duplicate_library_item", codes);
        Assert.Contains("duplicate_manual_link", codes);
    }

    [Fact]
    public async Task Restore_ExportedBackupIntoEmptyDatabase_PreservesLibraryState()
    {
        var (sourceDb, sourceConnection) = DatabaseFixture.CreateCoreContext();
        await using var sourceDbScope = sourceDb;
        await using var sourceConnectionScope = sourceConnection;

        var sharedTag = new Tag { Name = "FPS" };
        var doom = new UnifiedGame
        {
            Title = "DOOM",
            Slug = "doom",
            PlayStatus = PlayStatus.Completed,
            UserRating = 5,
            IsPlayStatusManual = true,
            CoverUrl = "https://images.example/doom.jpg",
            Summary = "Rip and tear.",
            Genres = "Shooter",
            Rating = 91,
            MetadataProviderId = 7351,
            MetadataSource = "IGDB",
            Tags = [sharedTag],
            Items =
            [
                new LibraryItem
                {
                    Platform = Platform.Steam,
                    ExternalId = "379720",
                    LastSynced = FixedNow.UtcDateTime.AddDays(-1)
                }
            ],
            PhysicalCopies =
            [
                new PhysicalGameCopy { Platform = PhysicalGamePlatform.PS5 },
                new PhysicalGameCopy { Platform = PhysicalGamePlatform.PS5 }
            ]
        };
        var quake = new UnifiedGame
        {
            Title = "Quake",
            Slug = "quake",
            PlayStatus = PlayStatus.Playing,
            Tags = [sharedTag],
            Items =
            [
                new LibraryItem
                {
                    Platform = Platform.Gog,
                    ExternalId = "1440166133",
                    LastSynced = FixedNow.UtcDateTime.AddDays(-2)
                }
            ]
        };
        sourceDb.UnifiedGames.AddRange(doom, quake);
        await sourceDb.SaveChangesAsync();
        sourceDb.ManualLinks.Add(new ManualLink
        {
            Platform = Platform.Steam,
            ExternalId = "379720",
            UnifiedGameId = doom.Id
        });
        await sourceDb.SaveChangesAsync();

        var exporter = new BackupExportService(sourceDb, new FixedTimeProvider(FixedNow));
        var exported = await exporter.CreateBackupAsync();
        await using var stream = new MemoryStream(_serializer.Serialize(exported));
        var (parsed, validation) = await _service.ReadAndValidateAsync(stream);

        Assert.True(validation.IsValid);
        Assert.Equal(2, validation.Summary.PhysicalCopyCount);
        var restore = await _service.RestoreAsync(parsed!);

        Assert.True(restore.Restored);
        Assert.Equal(2, restore.Summary.GameCount);

        var restoredGames = await _db.UnifiedGames
            .Include(game => game.Items)
            .Include(game => game.PhysicalCopies)
            .Include(game => game.Tags)
            .OrderBy(game => game.Slug)
            .ToListAsync();
        Assert.Equal(2, restoredGames.Count);

        var restoredDoom = restoredGames[0];
        Assert.Equal("DOOM", restoredDoom.Title);
        Assert.Equal(PlayStatus.Completed, restoredDoom.PlayStatus);
        Assert.Equal(5, restoredDoom.UserRating);
        Assert.True(restoredDoom.IsPlayStatusManual);
        Assert.Equal("Rip and tear.", restoredDoom.Summary);
        Assert.Equal(7351, restoredDoom.MetadataProviderId);
        Assert.Equal("IGDB", restoredDoom.MetadataSource);
        Assert.Equal("379720", Assert.Single(restoredDoom.Items).ExternalId);
        Assert.Equal("FPS", Assert.Single(restoredDoom.Tags).Name);
        Assert.Equal(2, restoredDoom.PhysicalCopies.Count);
        Assert.All(restoredDoom.PhysicalCopies, copy => Assert.Equal(PhysicalGamePlatform.PS5, copy.Platform));

        Assert.Equal(1, await _db.Tags.CountAsync());
        var restoredLink = await _db.ManualLinks.SingleAsync();
        Assert.Equal(restoredDoom.Id, restoredLink.UnifiedGameId);
        Assert.Equal("379720", restoredLink.ExternalId);

        var activity = await _db.ActivityLogs.SingleAsync();
        Assert.Equal("BackupRestore", activity.EventType);
        Assert.Equal(FixedNow.UtcDateTime, activity.Timestamp);
    }

    [Fact]
    public async Task Restore_TargetLibraryNotEmpty_ReturnsConflictWithoutChanges()
    {
        _db.UnifiedGames.Add(new UnifiedGame { Title = "Existing", Slug = "existing" });
        await _db.SaveChangesAsync();
        var backup = CreateBackup(CreateGame("new-game", "New Game", "42", ["Action"]));

        var result = await _service.RestoreAsync(backup);

        Assert.False(result.Restored);
        Assert.Equal("The target library must be empty before restore.", result.Message);
        Assert.Equal(1, await _db.UnifiedGames.CountAsync());
        Assert.NotNull(await _db.UnifiedGames.SingleOrDefaultAsync(game => game.Slug == "existing"));
        Assert.Empty(_db.ActivityLogs);
    }

    [Fact]
    public async Task Restore_V1BackupWithoutPhysicalCopies_RemainsSupported()
    {
        const string v1Json = """
            {
              "format": "gamehub-backup",
              "formatVersion": 1,
              "exportedAtUtc": "2026-06-19T14:00:00Z",
              "games": [
                {
                  "title": "Legacy",
                  "slug": "legacy",
                  "personalTracking": {
                    "playStatus": "Unplayed",
                    "userRating": null,
                    "isPlayStatusManual": false,
                    "isIgnored": false
                  },
                  "metadata": {
                    "coverUrl": null,
                    "summary": null,
                    "genres": null,
                    "platforms": null,
                    "artworks": null,
                    "screenshots": null,
                    "developers": null,
                    "publishers": null,
                    "firstReleaseDate": null,
                    "rating": null,
                    "metadataProviderId": null,
                    "metadataSource": null,
                    "enrichedAt": null
                  },
                  "tags": [],
                  "items": [],
                  "manualLinks": []
                }
              ]
            }
            """;
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(v1Json));

        var (v1, validation) = await _service.ReadAndValidateAsync(stream);
        var result = await _service.RestoreAsync(v1!);

        Assert.NotNull(v1);
        Assert.True(validation.IsValid);
        Assert.True(result.Restored);
        Assert.Empty(_db.PhysicalGameCopies);
        Assert.Equal("Legacy", (await _db.UnifiedGames.SingleAsync()).Title);
    }

    [Fact]
    public async Task Validate_MissingUpload_ReturnsBadRequest()
    {
        var controller = new BackupImportController(
            _service,
            NullLogger<BackupImportController>.Instance);

        var result = await controller.Validate(null, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var validation = Assert.IsType<BackupValidationResult>(badRequest.Value);
        Assert.Equal("missing_file", Assert.Single(validation.Errors).Code);
    }

    [Fact]
    public async Task Validate_ValidUpload_ReturnsOkPreview()
    {
        var bytes = _serializer.Serialize(
            CreateBackup(CreateGame("doom", "DOOM", "379720", ["Action"])));
        var file = CreateFormFile(bytes);
        var controller = new BackupImportController(
            _service,
            NullLogger<BackupImportController>.Instance);

        var result = await controller.Validate(file, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var validation = Assert.IsType<BackupValidationResult>(ok.Value);
        Assert.True(validation.IsValid);
        Assert.Equal(1, validation.Summary.GameCount);
        Assert.Equal(1, validation.Summary.PlatformItemCount);
    }

    [Fact]
    public async Task Restore_TargetLibraryNotEmpty_ReturnsHttpConflict()
    {
        _db.UnifiedGames.Add(new UnifiedGame { Title = "Existing", Slug = "existing" });
        await _db.SaveChangesAsync();
        var bytes = _serializer.Serialize(
            CreateBackup(CreateGame("new-game", "New Game", "42", ["Action"])));
        var file = CreateFormFile(bytes);
        var controller = new BackupImportController(
            _service,
            NullLogger<BackupImportController>.Instance);

        var result = await controller.Restore(file, CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        var restore = Assert.IsType<BackupRestoreResult>(conflict.Value);
        Assert.False(restore.Restored);
    }

    private static GameHubBackupV1 CreateBackup(params BackupGameV1[] games) =>
        new(
            BackupExportService.FormatName,
            BackupExportService.CurrentFormatVersion,
            FixedNow,
            games);

    private static BackupGameV1 CreateGame(
        string slug,
        string title,
        string externalId,
        IReadOnlyList<string> tags) =>
        new(
            title,
            slug,
            new BackupPersonalTrackingV1(PlayStatus.Unplayed, null, false, false),
            new BackupMetadataV1(
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null),
            tags,
            [new BackupLibraryItemV1(Platform.Steam, externalId, FixedNow.UtcDateTime)],
            [new BackupManualLinkV1(Platform.Steam, externalId)],
            []);

    private static FormFile CreateFormFile(byte[] content)
    {
        var stream = new MemoryStream(content);
        return new FormFile(stream, 0, content.Length, "file", "backup.json")
        {
            Headers = new HeaderDictionary(),
            ContentType = "application/json"
        };
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
