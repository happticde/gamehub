using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameHub.Ingest.Migrations
{
    /// <inheritdoc />
    public partial class AddRawContentHashes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ContentHash",
                table: "SteamRawItems",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ContentHash",
                table: "PsnRawItems",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ContentHash",
                table: "GogRawItems",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ContentHash",
                table: "EpicRawItems",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_SteamRawItems_TenantId_LastUpdated",
                table: "SteamRawItems",
                columns: new[] { "TenantId", "LastUpdated" });

            migrationBuilder.CreateIndex(
                name: "IX_PsnRawItems_TenantId_LastUpdated",
                table: "PsnRawItems",
                columns: new[] { "TenantId", "LastUpdated" });

            migrationBuilder.CreateIndex(
                name: "IX_GogRawItems_TenantId_LastUpdated",
                table: "GogRawItems",
                columns: new[] { "TenantId", "LastUpdated" });

            migrationBuilder.CreateIndex(
                name: "IX_EpicRawItems_TenantId_LastUpdated",
                table: "EpicRawItems",
                columns: new[] { "TenantId", "LastUpdated" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SteamRawItems_TenantId_LastUpdated",
                table: "SteamRawItems");

            migrationBuilder.DropIndex(
                name: "IX_PsnRawItems_TenantId_LastUpdated",
                table: "PsnRawItems");

            migrationBuilder.DropIndex(
                name: "IX_GogRawItems_TenantId_LastUpdated",
                table: "GogRawItems");

            migrationBuilder.DropIndex(
                name: "IX_EpicRawItems_TenantId_LastUpdated",
                table: "EpicRawItems");

            migrationBuilder.DropColumn(
                name: "ContentHash",
                table: "SteamRawItems");

            migrationBuilder.DropColumn(
                name: "ContentHash",
                table: "PsnRawItems");

            migrationBuilder.DropColumn(
                name: "ContentHash",
                table: "GogRawItems");

            migrationBuilder.DropColumn(
                name: "ContentHash",
                table: "EpicRawItems");
        }
    }
}
