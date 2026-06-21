using GameHub.Core.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameHub.Core.Migrations.postgres;

/// <inheritdoc />
[DbContext(typeof(CoreContext))]
[Migration("20260621110000_EnforceTenantGameRelationshipsPostgres")]
public partial class EnforceTenantGameRelationshipsPostgres : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        if (migrationBuilder.ActiveProvider != "Npgsql.EntityFrameworkCore.PostgreSQL")
        {
            return;
        }

        DropLegacyGameRelationships(migrationBuilder);
        AddTenantGameRelationships(migrationBuilder);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        if (migrationBuilder.ActiveProvider != "Npgsql.EntityFrameworkCore.PostgreSQL")
        {
            return;
        }

        DropTenantGameRelationships(migrationBuilder);
        AddLegacyGameRelationships(migrationBuilder);
    }

    private static void DropLegacyGameRelationships(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_EnrichmentAuditLogs_UnifiedGames_UnifiedGameId",
            table: "EnrichmentAuditLogs");
        migrationBuilder.DropForeignKey(
            name: "FK_LibraryItems_UnifiedGames_UnifiedGameId",
            table: "LibraryItems");
        migrationBuilder.DropForeignKey(
            name: "FK_ManualLinks_UnifiedGames_UnifiedGameId",
            table: "ManualLinks");
        migrationBuilder.DropIndex(
            name: "IX_EnrichmentAuditLogs_UnifiedGameId",
            table: "EnrichmentAuditLogs");
        migrationBuilder.DropIndex(
            name: "IX_LibraryItems_UnifiedGameId",
            table: "LibraryItems");
        migrationBuilder.DropIndex(
            name: "IX_ManualLinks_UnifiedGameId",
            table: "ManualLinks");
    }

    private static void AddTenantGameRelationships(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddUniqueConstraint(
            name: "AK_UnifiedGames_TenantId_Id",
            table: "UnifiedGames",
            columns: new[] { "TenantId", "Id" });
        migrationBuilder.CreateIndex(
            name: "IX_EnrichmentAuditLogs_TenantId_UnifiedGameId",
            table: "EnrichmentAuditLogs",
            columns: new[] { "TenantId", "UnifiedGameId" });
        migrationBuilder.CreateIndex(
            name: "IX_ManualLinks_TenantId_UnifiedGameId",
            table: "ManualLinks",
            columns: new[] { "TenantId", "UnifiedGameId" });
        migrationBuilder.AddForeignKey(
            name: "FK_EnrichmentAuditLogs_UnifiedGames_TenantId_UnifiedGameId",
            table: "EnrichmentAuditLogs",
            columns: new[] { "TenantId", "UnifiedGameId" },
            principalTable: "UnifiedGames",
            principalColumns: new[] { "TenantId", "Id" },
            onDelete: ReferentialAction.Cascade);
        migrationBuilder.AddForeignKey(
            name: "FK_LibraryItems_UnifiedGames_TenantId_UnifiedGameId",
            table: "LibraryItems",
            columns: new[] { "TenantId", "UnifiedGameId" },
            principalTable: "UnifiedGames",
            principalColumns: new[] { "TenantId", "Id" },
            onDelete: ReferentialAction.Cascade);
        migrationBuilder.AddForeignKey(
            name: "FK_ManualLinks_UnifiedGames_TenantId_UnifiedGameId",
            table: "ManualLinks",
            columns: new[] { "TenantId", "UnifiedGameId" },
            principalTable: "UnifiedGames",
            principalColumns: new[] { "TenantId", "Id" },
            onDelete: ReferentialAction.Cascade);
    }

    private static void DropTenantGameRelationships(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_EnrichmentAuditLogs_UnifiedGames_TenantId_UnifiedGameId",
            table: "EnrichmentAuditLogs");
        migrationBuilder.DropForeignKey(
            name: "FK_LibraryItems_UnifiedGames_TenantId_UnifiedGameId",
            table: "LibraryItems");
        migrationBuilder.DropForeignKey(
            name: "FK_ManualLinks_UnifiedGames_TenantId_UnifiedGameId",
            table: "ManualLinks");
        migrationBuilder.DropIndex(
            name: "IX_EnrichmentAuditLogs_TenantId_UnifiedGameId",
            table: "EnrichmentAuditLogs");
        migrationBuilder.DropIndex(
            name: "IX_ManualLinks_TenantId_UnifiedGameId",
            table: "ManualLinks");
        migrationBuilder.DropUniqueConstraint(
            name: "AK_UnifiedGames_TenantId_Id",
            table: "UnifiedGames");
    }

    private static void AddLegacyGameRelationships(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex(
            name: "IX_EnrichmentAuditLogs_UnifiedGameId",
            table: "EnrichmentAuditLogs",
            column: "UnifiedGameId");
        migrationBuilder.CreateIndex(
            name: "IX_LibraryItems_UnifiedGameId",
            table: "LibraryItems",
            column: "UnifiedGameId");
        migrationBuilder.CreateIndex(
            name: "IX_ManualLinks_UnifiedGameId",
            table: "ManualLinks",
            column: "UnifiedGameId");
        migrationBuilder.AddForeignKey(
            name: "FK_EnrichmentAuditLogs_UnifiedGames_UnifiedGameId",
            table: "EnrichmentAuditLogs",
            column: "UnifiedGameId",
            principalTable: "UnifiedGames",
            principalColumn: "Id",
            onDelete: ReferentialAction.Cascade);
        migrationBuilder.AddForeignKey(
            name: "FK_LibraryItems_UnifiedGames_UnifiedGameId",
            table: "LibraryItems",
            column: "UnifiedGameId",
            principalTable: "UnifiedGames",
            principalColumn: "Id",
            onDelete: ReferentialAction.Cascade);
        migrationBuilder.AddForeignKey(
            name: "FK_ManualLinks_UnifiedGames_UnifiedGameId",
            table: "ManualLinks",
            column: "UnifiedGameId",
            principalTable: "UnifiedGames",
            principalColumn: "Id",
            onDelete: ReferentialAction.Cascade);
    }
}
