using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameHub.Core.Migrations.sqlite
{
    /// <inheritdoc />
    public partial class AddTenantFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider != "Microsoft.EntityFrameworkCore.Sqlite")
            {
                return;
            }

            migrationBuilder.DropIndex(
                name: "IX_UnifiedGames_Slug",
                table: "UnifiedGames");

            migrationBuilder.DropIndex(
                name: "IX_Tags_Name",
                table: "Tags");

            migrationBuilder.DropIndex(
                name: "IX_SyncMetadata_Platform",
                table: "SyncMetadata");

            migrationBuilder.DropIndex(
                name: "IX_ManualLinks_Platform_ExternalId",
                table: "ManualLinks");

            migrationBuilder.DropIndex(
                name: "IX_LibraryItems_UnifiedGameId_Platform",
                table: "LibraryItems");

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "UnifiedGames",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "Tags",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "SyncMetadata",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "SyncAuditLogs",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "ManualLinks",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "LibraryItems",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "EnrichmentAuditLogs",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "ActivityLogs",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.CreateTable(
                name: "Tenants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tenants", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "Tenants",
                columns: new[] { "Id", "CreatedAtUtc", "Name" },
                values: new object[] { new Guid("00000000-0000-0000-0000-000000000001"), new DateTime(2026, 6, 19, 0, 0, 0, 0, DateTimeKind.Utc), "Default" });

            migrationBuilder.CreateIndex(
                name: "IX_UnifiedGames_TenantId_Slug",
                table: "UnifiedGames",
                columns: new[] { "TenantId", "Slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tags_TenantId_Name",
                table: "Tags",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SyncMetadata_TenantId_Platform",
                table: "SyncMetadata",
                columns: new[] { "TenantId", "Platform" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SyncAuditLogs_TenantId",
                table: "SyncAuditLogs",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ManualLinks_TenantId_Platform_ExternalId",
                table: "ManualLinks",
                columns: new[] { "TenantId", "Platform", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LibraryItems_TenantId_UnifiedGameId_Platform",
                table: "LibraryItems",
                columns: new[] { "TenantId", "UnifiedGameId", "Platform" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LibraryItems_UnifiedGameId",
                table: "LibraryItems",
                column: "UnifiedGameId");

            migrationBuilder.CreateIndex(
                name: "IX_EnrichmentAuditLogs_TenantId",
                table: "EnrichmentAuditLogs",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ActivityLogs_TenantId",
                table: "ActivityLogs",
                column: "TenantId");

            migrationBuilder.AddForeignKey(
                name: "FK_ActivityLogs_Tenants_TenantId",
                table: "ActivityLogs",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_EnrichmentAuditLogs_Tenants_TenantId",
                table: "EnrichmentAuditLogs",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_LibraryItems_Tenants_TenantId",
                table: "LibraryItems",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ManualLinks_Tenants_TenantId",
                table: "ManualLinks",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SyncAuditLogs_Tenants_TenantId",
                table: "SyncAuditLogs",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SyncMetadata_Tenants_TenantId",
                table: "SyncMetadata",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Tags_Tenants_TenantId",
                table: "Tags",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_UnifiedGames_Tenants_TenantId",
                table: "UnifiedGames",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider != "Microsoft.EntityFrameworkCore.Sqlite")
            {
                return;
            }

            migrationBuilder.DropForeignKey(
                name: "FK_ActivityLogs_Tenants_TenantId",
                table: "ActivityLogs");

            migrationBuilder.DropForeignKey(
                name: "FK_EnrichmentAuditLogs_Tenants_TenantId",
                table: "EnrichmentAuditLogs");

            migrationBuilder.DropForeignKey(
                name: "FK_LibraryItems_Tenants_TenantId",
                table: "LibraryItems");

            migrationBuilder.DropForeignKey(
                name: "FK_ManualLinks_Tenants_TenantId",
                table: "ManualLinks");

            migrationBuilder.DropForeignKey(
                name: "FK_SyncAuditLogs_Tenants_TenantId",
                table: "SyncAuditLogs");

            migrationBuilder.DropForeignKey(
                name: "FK_SyncMetadata_Tenants_TenantId",
                table: "SyncMetadata");

            migrationBuilder.DropForeignKey(
                name: "FK_Tags_Tenants_TenantId",
                table: "Tags");

            migrationBuilder.DropForeignKey(
                name: "FK_UnifiedGames_Tenants_TenantId",
                table: "UnifiedGames");

            migrationBuilder.DropTable(
                name: "Tenants");

            migrationBuilder.DropIndex(
                name: "IX_UnifiedGames_TenantId_Slug",
                table: "UnifiedGames");

            migrationBuilder.DropIndex(
                name: "IX_Tags_TenantId_Name",
                table: "Tags");

            migrationBuilder.DropIndex(
                name: "IX_SyncMetadata_TenantId_Platform",
                table: "SyncMetadata");

            migrationBuilder.DropIndex(
                name: "IX_SyncAuditLogs_TenantId",
                table: "SyncAuditLogs");

            migrationBuilder.DropIndex(
                name: "IX_ManualLinks_TenantId_Platform_ExternalId",
                table: "ManualLinks");

            migrationBuilder.DropIndex(
                name: "IX_LibraryItems_TenantId_UnifiedGameId_Platform",
                table: "LibraryItems");

            migrationBuilder.DropIndex(
                name: "IX_LibraryItems_UnifiedGameId",
                table: "LibraryItems");

            migrationBuilder.DropIndex(
                name: "IX_EnrichmentAuditLogs_TenantId",
                table: "EnrichmentAuditLogs");

            migrationBuilder.DropIndex(
                name: "IX_ActivityLogs_TenantId",
                table: "ActivityLogs");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "UnifiedGames");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "Tags");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "SyncMetadata");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "SyncAuditLogs");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "ManualLinks");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "LibraryItems");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "EnrichmentAuditLogs");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "ActivityLogs");

            migrationBuilder.CreateIndex(
                name: "IX_UnifiedGames_Slug",
                table: "UnifiedGames",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tags_Name",
                table: "Tags",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SyncMetadata_Platform",
                table: "SyncMetadata",
                column: "Platform",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ManualLinks_Platform_ExternalId",
                table: "ManualLinks",
                columns: new[] { "Platform", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LibraryItems_UnifiedGameId_Platform",
                table: "LibraryItems",
                columns: new[] { "UnifiedGameId", "Platform" },
                unique: true);
        }
    }
}
