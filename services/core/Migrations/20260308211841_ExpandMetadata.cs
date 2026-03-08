using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class ExpandMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Artworks",
                table: "UnifiedGames",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Developers",
                table: "UnifiedGames",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Platforms",
                table: "UnifiedGames",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Publishers",
                table: "UnifiedGames",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Screenshots",
                table: "UnifiedGames",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Artworks",
                table: "UnifiedGames");

            migrationBuilder.DropColumn(
                name: "Developers",
                table: "UnifiedGames");

            migrationBuilder.DropColumn(
                name: "Platforms",
                table: "UnifiedGames");

            migrationBuilder.DropColumn(
                name: "Publishers",
                table: "UnifiedGames");

            migrationBuilder.DropColumn(
                name: "Screenshots",
                table: "UnifiedGames");
        }
    }
}
