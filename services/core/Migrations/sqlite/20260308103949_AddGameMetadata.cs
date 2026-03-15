using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameHub.Core.Migrations.sqlite
{
    /// <inheritdoc />
    public partial class AddGameMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CoverUrl",
                table: "UnifiedGames",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "EnrichedAt",
                table: "UnifiedGames",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "FirstReleaseDate",
                table: "UnifiedGames",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Genres",
                table: "UnifiedGames",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MetadataProviderId",
                table: "UnifiedGames",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MetadataSource",
                table: "UnifiedGames",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "Rating",
                table: "UnifiedGames",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Summary",
                table: "UnifiedGames",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CoverUrl",
                table: "UnifiedGames");

            migrationBuilder.DropColumn(
                name: "EnrichedAt",
                table: "UnifiedGames");

            migrationBuilder.DropColumn(
                name: "FirstReleaseDate",
                table: "UnifiedGames");

            migrationBuilder.DropColumn(
                name: "Genres",
                table: "UnifiedGames");

            migrationBuilder.DropColumn(
                name: "MetadataProviderId",
                table: "UnifiedGames");

            migrationBuilder.DropColumn(
                name: "MetadataSource",
                table: "UnifiedGames");

            migrationBuilder.DropColumn(
                name: "Rating",
                table: "UnifiedGames");

            migrationBuilder.DropColumn(
                name: "Summary",
                table: "UnifiedGames");
        }
    }
}
