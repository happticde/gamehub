using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameHub.Ingest.Migrations
{
    /// <inheritdoc />
    public partial class AddIngestTenantIsolation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Ingest currently keeps one migration chain for both providers. Only the physical
            // GUID column type differs; index and default-value operations are provider-neutral.
            var tenantIdColumnType = migrationBuilder.ActiveProvider ==
                "Npgsql.EntityFrameworkCore.PostgreSQL"
                    ? "uuid"
                    : "TEXT";

            migrationBuilder.DropIndex(
                name: "IX_SteamRawItems_AppId",
                table: "SteamRawItems");

            migrationBuilder.DropIndex(
                name: "IX_PsnRawItems_TitleId",
                table: "PsnRawItems");

            migrationBuilder.DropIndex(
                name: "IX_GogRawItems_GameId",
                table: "GogRawItems");

            migrationBuilder.DropIndex(
                name: "IX_EpicRawItems_AppName",
                table: "EpicRawItems");

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "SteamRawItems",
                type: tenantIdColumnType,
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "PsnRawItems",
                type: tenantIdColumnType,
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "IngestAuditLogs",
                type: tenantIdColumnType,
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "GogRawItems",
                type: tenantIdColumnType,
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "EpicRawItems",
                type: tenantIdColumnType,
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.CreateIndex(
                name: "IX_SteamRawItems_TenantId_AppId",
                table: "SteamRawItems",
                columns: new[] { "TenantId", "AppId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PsnRawItems_TenantId_TitleId",
                table: "PsnRawItems",
                columns: new[] { "TenantId", "TitleId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GogRawItems_TenantId_GameId",
                table: "GogRawItems",
                columns: new[] { "TenantId", "GameId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EpicRawItems_TenantId_AppName",
                table: "EpicRawItems",
                columns: new[] { "TenantId", "AppName" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SteamRawItems_TenantId_AppId",
                table: "SteamRawItems");

            migrationBuilder.DropIndex(
                name: "IX_PsnRawItems_TenantId_TitleId",
                table: "PsnRawItems");

            migrationBuilder.DropIndex(
                name: "IX_GogRawItems_TenantId_GameId",
                table: "GogRawItems");

            migrationBuilder.DropIndex(
                name: "IX_EpicRawItems_TenantId_AppName",
                table: "EpicRawItems");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "SteamRawItems");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "PsnRawItems");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "IngestAuditLogs");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "GogRawItems");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "EpicRawItems");

            migrationBuilder.CreateIndex(
                name: "IX_SteamRawItems_AppId",
                table: "SteamRawItems",
                column: "AppId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PsnRawItems_TitleId",
                table: "PsnRawItems",
                column: "TitleId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GogRawItems_GameId",
                table: "GogRawItems",
                column: "GameId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EpicRawItems_AppName",
                table: "EpicRawItems",
                column: "AppName",
                unique: true);
        }
    }
}
