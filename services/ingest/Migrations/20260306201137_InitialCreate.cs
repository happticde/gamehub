using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameHub.Ingest.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EpicRawItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AppName = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: true),
                    RawJson = table.Column<string>(type: "TEXT", nullable: false),
                    LastUpdated = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EpicRawItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GogRawItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GameId = table.Column<long>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: true),
                    RawJson = table.Column<string>(type: "TEXT", nullable: false),
                    LastUpdated = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GogRawItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PsnRawItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TitleId = table.Column<string>(type: "TEXT", nullable: false),
                    TitleName = table.Column<string>(type: "TEXT", nullable: true),
                    RawJson = table.Column<string>(type: "TEXT", nullable: false),
                    LastUpdated = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PsnRawItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SteamRawItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AppId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: true),
                    RawJson = table.Column<string>(type: "TEXT", nullable: false),
                    LastUpdated = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SteamRawItems", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EpicRawItems_AppName",
                table: "EpicRawItems",
                column: "AppName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GogRawItems_GameId",
                table: "GogRawItems",
                column: "GameId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PsnRawItems_TitleId",
                table: "PsnRawItems",
                column: "TitleId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SteamRawItems_AppId",
                table: "SteamRawItems",
                column: "AppId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EpicRawItems");

            migrationBuilder.DropTable(
                name: "GogRawItems");

            migrationBuilder.DropTable(
                name: "PsnRawItems");

            migrationBuilder.DropTable(
                name: "SteamRawItems");
        }
    }
}
