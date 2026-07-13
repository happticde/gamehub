using System.Text;
using System.Text.Json;
using GameHub.Core.Controllers;
using GameHub.Core.Data;
using GameHub.Core.Models.Backup;
using GameHub.Core.Services;
using GameHub.Shared;
using GameHub.Tests.Helpers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GameHub.Tests;

/// <summary>
/// Integration tests for the versioned JSON backup export.
/// </summary>
public sealed class BackupExportTests : IDisposable
{
    private static readonly DateTimeOffset ExportedAt = new(2026, 6, 19, 10, 30, 0, TimeSpan.Zero);

    private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
    private readonly CoreContext _db;
    private readonly BackupExportService _service;
    private readonly BackupJsonSerializer _serializer = new();
    private readonly BackupCsvSerializer _csvSerializer = new();

    public BackupExportTests()
    {
        var (db, connection) = DatabaseFixture.CreateCoreContext();
        _db = db;
        _connection = connection;
        _service = new BackupExportService(_db, new FixedTimeProvider(ExportedAt));
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task CreateBackup_ProviderFreshnessOperationalState_IsExcludedFromV1()
    {
        _db.SyncMetadata.Add(new SyncMetadata
        {
            Platform = Platform.Epic,
            LastSyncedAt = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            LastSuccessfulProviderRetrievalAtUtc = new DateTime(2030, 1, 2, 3, 4, 5, DateTimeKind.Utc)
        });
        await _db.SaveChangesAsync();

        var json = Encoding.UTF8.GetString(_serializer.Serialize(await _service.CreateBackupAsync()));

        Assert.DoesNotContain("lastSuccessfulProviderRetrieval", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("2030-01-02", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateBackup_WithCompleteGame_PreservesDurableLibraryState()
    {
        var game = new UnifiedGame
        {
            Title = "The Witcher 3",
            Slug = "the-witcher-3",
            PlayStatus = PlayStatus.Completed,
            UserRating = 5,
            IsPlayStatusManual = true,
            IsIgnored = true,
            CoverUrl = "https://images.example/cover.jpg",
            Summary = "A story-driven RPG.",
            Genres = "RPG, Adventure",
            Platforms = "PC, PlayStation 5",
            Artworks = "https://images.example/art.jpg",
            Screenshots = "https://images.example/shot.jpg",
            Developers = "CD Projekt Red",
            Publishers = "CD Projekt",
            FirstReleaseDate = new DateTime(2015, 5, 19, 0, 0, 0, DateTimeKind.Utc),
            Rating = 92.5,
            MetadataProviderId = 1942,
            MetadataSource = "IGDB",
            EnrichedAt = new DateTime(2026, 6, 18, 12, 0, 0, DateTimeKind.Utc),
            Tags =
            [
                new Tag { Name = "Open World" },
                new Tag { Name = "RPG" }
            ],
            Items =
            [
                new LibraryItem
                {
                    Platform = Platform.Gog,
                    ExternalId = "1207664663",
                    LastSynced = new DateTime(2026, 6, 17, 9, 0, 0, DateTimeKind.Utc)
                },
                new LibraryItem
                {
                    Platform = Platform.Steam,
                    ExternalId = "292030",
                    LastSynced = new DateTime(2026, 6, 17, 8, 0, 0, DateTimeKind.Utc)
                }
            ],
            PhysicalCopies =
            [
                new PhysicalGameCopy { Platform = PhysicalGamePlatform.PS5 },
                new PhysicalGameCopy { Platform = PhysicalGamePlatform.PS3 }
            ]
        };
        _db.UnifiedGames.Add(game);
        await _db.SaveChangesAsync();

        _db.ManualLinks.Add(new ManualLink
        {
            Platform = Platform.Steam,
            ExternalId = "292030",
            UnifiedGameId = game.Id
        });
        await _db.SaveChangesAsync();

        var backup = await _service.CreateBackupAsync();

        Assert.Equal(BackupExportService.FormatName, backup.Format);
        Assert.Equal(BackupExportService.CurrentFormatVersion, backup.FormatVersion);
        Assert.Equal(ExportedAt, backup.ExportedAtUtc);

        var exportedGame = Assert.Single(backup.Games);
        Assert.Equal(game.Title, exportedGame.Title);
        Assert.Equal(game.Slug, exportedGame.Slug);
        Assert.Equal(PlayStatus.Completed, exportedGame.PersonalTracking.PlayStatus);
        Assert.Equal(5, exportedGame.PersonalTracking.UserRating);
        Assert.True(exportedGame.PersonalTracking.IsPlayStatusManual);
        Assert.True(exportedGame.PersonalTracking.IsIgnored);
        Assert.Equal(game.CoverUrl, exportedGame.Metadata.CoverUrl);
        Assert.Equal(game.Summary, exportedGame.Metadata.Summary);
        Assert.Equal(game.Genres, exportedGame.Metadata.Genres);
        Assert.Equal(game.Platforms, exportedGame.Metadata.Platforms);
        Assert.Equal(game.Artworks, exportedGame.Metadata.Artworks);
        Assert.Equal(game.Screenshots, exportedGame.Metadata.Screenshots);
        Assert.Equal(game.Developers, exportedGame.Metadata.Developers);
        Assert.Equal(game.Publishers, exportedGame.Metadata.Publishers);
        Assert.Equal(game.FirstReleaseDate, exportedGame.Metadata.FirstReleaseDate);
        Assert.Equal(game.Rating, exportedGame.Metadata.Rating);
        Assert.Equal(game.MetadataProviderId, exportedGame.Metadata.MetadataProviderId);
        Assert.Equal(game.MetadataSource, exportedGame.Metadata.MetadataSource);
        Assert.Equal(game.EnrichedAt, exportedGame.Metadata.EnrichedAt);
        Assert.Equal(["Open World", "RPG"], exportedGame.Tags);
        Assert.Equal(2, exportedGame.Items.Count);
        Assert.Equal(Platform.Steam, exportedGame.Items[0].Platform);
        Assert.Equal("292030", exportedGame.Items[0].ExternalId);
        Assert.Equal(game.Items.Single(item => item.Platform == Platform.Steam).LastSynced, exportedGame.Items[0].LastSynced);
        Assert.Equal(Platform.Gog, exportedGame.Items[1].Platform);
        Assert.Equal("1207664663", exportedGame.Items[1].ExternalId);
        Assert.Equal(game.Items.Single(item => item.Platform == Platform.Gog).LastSynced, exportedGame.Items[1].LastSynced);

        var manualLink = Assert.Single(exportedGame.ManualLinks);
        Assert.Equal(Platform.Steam, manualLink.Platform);
        Assert.Equal("292030", manualLink.ExternalId);
        Assert.Equal(
            [PhysicalGamePlatform.PS3, PhysicalGamePlatform.PS5],
            exportedGame.PhysicalCopies!.Select(copy => copy.Platform));
    }

    [Fact]
    public async Task CreateBackup_WithMultipleGames_ReturnsDeterministicOrder()
    {
        _db.UnifiedGames.AddRange(
            new UnifiedGame { Title = "Zulu", Slug = "zulu" },
            new UnifiedGame { Title = "Alpha", Slug = "alpha" });
        await _db.SaveChangesAsync();

        var backup = await _service.CreateBackupAsync();

        Assert.Equal(["alpha", "zulu"], backup.Games.Select(game => game.Slug));
    }

    [Fact]
    public async Task Export_WithGame_ReturnsDownloadableJsonUsingStringEnums()
    {
        _db.UnifiedGames.Add(new UnifiedGame
        {
            Title = "DOOM",
            Slug = "doom",
            PlayStatus = PlayStatus.Playing,
            Items =
            [
                new LibraryItem { Platform = Platform.Steam, ExternalId = "379720" }
            ]
        });
        await _db.SaveChangesAsync();

        var controller = new BackupController(
            _service,
            _serializer,
            _csvSerializer,
            NullLogger<BackupController>.Instance);

        var result = await controller.Export(CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("application/json; charset=utf-8", file.ContentType);
        Assert.Equal("gamehub-backup-v2-20260619-103000.json", file.FileDownloadName);

        using var json = JsonDocument.Parse(file.FileContents);
        var root = json.RootElement;
        Assert.Equal("gamehub-backup", root.GetProperty("format").GetString());
        Assert.Equal(2, root.GetProperty("formatVersion").GetInt32());

        var exportedGame = root.GetProperty("games")[0];
        Assert.Equal("Playing", exportedGame.GetProperty("personalTracking").GetProperty("playStatus").GetString());
        Assert.Equal("Steam", exportedGame.GetProperty("items")[0].GetProperty("platform").GetString());
        Assert.Equal(0, exportedGame.GetProperty("physicalCopies").GetArrayLength());
        Assert.False(exportedGame.TryGetProperty("id", out _));
        Assert.False(exportedGame.GetProperty("items")[0].TryGetProperty("id", out _));
    }

    [Fact]
    public void SerializeCsv_WithSpecialCharacters_ProducesSafeSpreadsheetOutput()
    {
        var backup = new GameHubBackupV1(
            BackupExportService.FormatName,
            BackupExportService.CurrentFormatVersion,
            ExportedAt,
            [
                new BackupGameV1(
                    "=2+2",
                    "formula-game",
                    new BackupPersonalTrackingV1(
                        PlayStatus.Completed,
                        5,
                        true,
                        false),
                    new BackupMetadataV1(
                        "https://images.example/cover.jpg",
                        "Line 1, \"quoted\"\nLine 2",
                        "RPG, Adventure",
                        "PC",
                        null,
                        null,
                        "Studio",
                        "Publisher",
                        new DateTime(2020, 1, 2),
                        92.5,
                        123,
                        "IGDB",
                        new DateTime(2026, 6, 19, 9, 30, 0, DateTimeKind.Utc)),
                    ["RPG", "Adventure"],
                    [
                        new BackupLibraryItemV1(
                            Platform.Steam,
                            "42",
                            new DateTime(2026, 6, 18, 8, 0, 0, DateTimeKind.Utc))
                    ],
                    [new BackupManualLinkV1(Platform.Steam, "42")],
                    [new BackupPhysicalGameCopyV2(PhysicalGamePlatform.PS4)])
            ]);

        var bytes = _csvSerializer.Serialize(backup);
        var csv = Encoding.UTF8.GetString(bytes.AsSpan(3));

        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        Assert.StartsWith("Title,Slug,Play Status,User Rating", csv);
        Assert.Contains("\r\n'=2+2,formula-game,Completed,5,True,False", csv);
        Assert.Contains("Steam:42", csv);
        Assert.Contains("PS4", csv);
        Assert.Contains("92.5,IGDB,123", csv);
        Assert.Contains("\"Line 1, \"\"quoted\"\"\nLine 2\"\r\n", csv);
    }

    [Fact]
    public async Task ExportCsv_WithGame_ReturnsDownloadableUtf8Csv()
    {
        _db.UnifiedGames.Add(new UnifiedGame
        {
            Title = "DOOM",
            Slug = "doom",
            Items = [new LibraryItem { Platform = Platform.Steam, ExternalId = "379720" }]
        });
        await _db.SaveChangesAsync();
        var controller = new BackupController(
            _service,
            _serializer,
            _csvSerializer,
            NullLogger<BackupController>.Instance);

        var result = await controller.ExportCsv(CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("text/csv; charset=utf-8", file.ContentType);
        Assert.Equal("gamehub-library-20260619-103000.csv", file.FileDownloadName);
        Assert.Equal([0xEF, 0xBB, 0xBF], file.FileContents[..3]);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
