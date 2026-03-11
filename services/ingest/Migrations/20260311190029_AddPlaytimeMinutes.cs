using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameHub.Ingest.Migrations
{
    /// <inheritdoc />
    public partial class AddPlaytimeMinutes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PlaytimeMinutes",
                table: "SteamRawItems",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PlaytimeMinutes",
                table: "PsnRawItems",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PlaytimeMinutes",
                table: "GogRawItems",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PlaytimeMinutes",
                table: "EpicRawItems",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PlaytimeMinutes",
                table: "SteamRawItems");

            migrationBuilder.DropColumn(
                name: "PlaytimeMinutes",
                table: "PsnRawItems");

            migrationBuilder.DropColumn(
                name: "PlaytimeMinutes",
                table: "GogRawItems");

            migrationBuilder.DropColumn(
                name: "PlaytimeMinutes",
                table: "EpicRawItems");
        }
    }
}
