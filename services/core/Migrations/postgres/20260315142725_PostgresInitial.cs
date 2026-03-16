using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace GameHub.Core.Migrations.postgres
{
    /// <inheritdoc />
    public partial class PostgresInitial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider != "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                return;
            }

            migrationBuilder.CreateTable(
                name: "SyncAuditLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Platform = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    ItemsProcessed = table.Column<int>(type: "integer", nullable: false),
                    NewGamesCreated = table.Column<int>(type: "integer", nullable: false),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncAuditLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SyncMetadata",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Platform = table.Column<int>(type: "integer", nullable: false),
                    LastSyncedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncMetadata", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Tags",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tags", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UnifiedGames",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Title = table.Column<string>(type: "text", nullable: false),
                    Slug = table.Column<string>(type: "text", nullable: false),
                    PlayStatus = table.Column<int>(type: "integer", nullable: false),
                    UserRating = table.Column<int>(type: "integer", nullable: true),
                    IsPlayStatusManual = table.Column<bool>(type: "boolean", nullable: false),
                    IsIgnored = table.Column<bool>(type: "boolean", nullable: false),
                    CoverUrl = table.Column<string>(type: "text", nullable: true),
                    Summary = table.Column<string>(type: "text", nullable: true),
                    Genres = table.Column<string>(type: "text", nullable: true),
                    Platforms = table.Column<string>(type: "text", nullable: true),
                    Artworks = table.Column<string>(type: "text", nullable: true),
                    Screenshots = table.Column<string>(type: "text", nullable: true),
                    Developers = table.Column<string>(type: "text", nullable: true),
                    Publishers = table.Column<string>(type: "text", nullable: true),
                    FirstReleaseDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Rating = table.Column<double>(type: "double precision", nullable: true),
                    MetadataProviderId = table.Column<int>(type: "integer", nullable: true),
                    MetadataSource = table.Column<string>(type: "text", nullable: true),
                    EnrichedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UnifiedGames", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EnrichmentAuditLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UnifiedGameId = table.Column<int>(type: "integer", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ProviderName = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true),
                    ProviderGameId = table.Column<int>(type: "integer", nullable: true),
                    MatchedTitle = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EnrichmentAuditLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EnrichmentAuditLogs_UnifiedGames_UnifiedGameId",
                        column: x => x.UnifiedGameId,
                        principalTable: "UnifiedGames",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LibraryItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UnifiedGameId = table.Column<int>(type: "integer", nullable: false),
                    Platform = table.Column<int>(type: "integer", nullable: false),
                    ExternalId = table.Column<string>(type: "text", nullable: false),
                    LastSynced = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LibraryItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LibraryItems_UnifiedGames_UnifiedGameId",
                        column: x => x.UnifiedGameId,
                        principalTable: "UnifiedGames",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ManualLinks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Platform = table.Column<int>(type: "integer", nullable: false),
                    ExternalId = table.Column<string>(type: "text", nullable: false),
                    UnifiedGameId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManualLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ManualLinks_UnifiedGames_UnifiedGameId",
                        column: x => x.UnifiedGameId,
                        principalTable: "UnifiedGames",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TagUnifiedGame",
                columns: table => new
                {
                    GamesId = table.Column<int>(type: "integer", nullable: false),
                    TagsId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TagUnifiedGame", x => new { x.GamesId, x.TagsId });
                    table.ForeignKey(
                        name: "FK_TagUnifiedGame_Tags_TagsId",
                        column: x => x.TagsId,
                        principalTable: "Tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TagUnifiedGame_UnifiedGames_GamesId",
                        column: x => x.GamesId,
                        principalTable: "UnifiedGames",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EnrichmentAuditLogs_UnifiedGameId",
                table: "EnrichmentAuditLogs",
                column: "UnifiedGameId");

            migrationBuilder.CreateIndex(
                name: "IX_LibraryItems_UnifiedGameId_Platform",
                table: "LibraryItems",
                columns: new[] { "UnifiedGameId", "Platform" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ManualLinks_Platform_ExternalId",
                table: "ManualLinks",
                columns: new[] { "Platform", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ManualLinks_UnifiedGameId",
                table: "ManualLinks",
                column: "UnifiedGameId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncMetadata_Platform",
                table: "SyncMetadata",
                column: "Platform",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tags_Name",
                table: "Tags",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TagUnifiedGame_TagsId",
                table: "TagUnifiedGame",
                column: "TagsId");

            migrationBuilder.CreateIndex(
                name: "IX_UnifiedGames_Slug",
                table: "UnifiedGames",
                column: "Slug",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider != "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                return;
            }

            migrationBuilder.DropTable(
                name: "EnrichmentAuditLogs");

            migrationBuilder.DropTable(
                name: "LibraryItems");

            migrationBuilder.DropTable(
                name: "ManualLinks");

            migrationBuilder.DropTable(
                name: "SyncAuditLogs");

            migrationBuilder.DropTable(
                name: "SyncMetadata");

            migrationBuilder.DropTable(
                name: "TagUnifiedGame");

            migrationBuilder.DropTable(
                name: "Tags");

            migrationBuilder.DropTable(
                name: "UnifiedGames");
        }
    }
}
