using GameHub.Core.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameHub.Core.Migrations.sqlite;

/// <inheritdoc />
[DbContext(typeof(CoreContext))]
[Migration("20260621110001_EnforceTenantGameRelationships")]
public partial class EnforceTenantGameRelationships : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        if (migrationBuilder.ActiveProvider != "Microsoft.EntityFrameworkCore.Sqlite")
        {
            return;
        }

        migrationBuilder.CreateIndex(
            name: "UX_UnifiedGames_TenantId_Id",
            table: "UnifiedGames",
            columns: new[] { "TenantId", "Id" },
            unique: true);
        migrationBuilder.CreateIndex(
            name: "IX_EnrichmentAuditLogs_TenantId_UnifiedGameId",
            table: "EnrichmentAuditLogs",
            columns: new[] { "TenantId", "UnifiedGameId" });
        migrationBuilder.CreateIndex(
            name: "IX_ManualLinks_TenantId_UnifiedGameId",
            table: "ManualLinks",
            columns: new[] { "TenantId", "UnifiedGameId" });

        CreateTenantGameGuardTriggers(migrationBuilder, "EnrichmentAuditLogs");
        CreateTenantGameGuardTriggers(migrationBuilder, "LibraryItems");
        CreateTenantGameGuardTriggers(migrationBuilder, "ManualLinks");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        if (migrationBuilder.ActiveProvider != "Microsoft.EntityFrameworkCore.Sqlite")
        {
            return;
        }

        DropTenantGameGuardTriggers(migrationBuilder, "EnrichmentAuditLogs");
        DropTenantGameGuardTriggers(migrationBuilder, "LibraryItems");
        DropTenantGameGuardTriggers(migrationBuilder, "ManualLinks");

        migrationBuilder.DropIndex(
            name: "IX_EnrichmentAuditLogs_TenantId_UnifiedGameId",
            table: "EnrichmentAuditLogs");
        migrationBuilder.DropIndex(
            name: "IX_ManualLinks_TenantId_UnifiedGameId",
            table: "ManualLinks");
        migrationBuilder.DropIndex(
            name: "UX_UnifiedGames_TenantId_Id",
            table: "UnifiedGames");
    }

    private static void CreateTenantGameGuardTriggers(MigrationBuilder migrationBuilder, string table)
    {
        migrationBuilder.Sql($$"""
            CREATE TRIGGER "TR_{{table}}_TenantGame_Insert"
            BEFORE INSERT ON "{{table}}"
            FOR EACH ROW
            WHEN NOT EXISTS (
                SELECT 1
                FROM "UnifiedGames"
                WHERE "TenantId" = NEW."TenantId"
                  AND "Id" = NEW."UnifiedGameId"
            )
            BEGIN
                SELECT RAISE(ABORT, 'Cross-tenant game relationship is not allowed.');
            END;
            """);

        migrationBuilder.Sql($$"""
            CREATE TRIGGER "TR_{{table}}_TenantGame_Update"
            BEFORE UPDATE OF "TenantId", "UnifiedGameId" ON "{{table}}"
            FOR EACH ROW
            WHEN NOT EXISTS (
                SELECT 1
                FROM "UnifiedGames"
                WHERE "TenantId" = NEW."TenantId"
                  AND "Id" = NEW."UnifiedGameId"
            )
            BEGIN
                SELECT RAISE(ABORT, 'Cross-tenant game relationship is not allowed.');
            END;
            """);
    }

    private static void DropTenantGameGuardTriggers(MigrationBuilder migrationBuilder, string table)
    {
        migrationBuilder.Sql($"DROP TRIGGER IF EXISTS \"TR_{table}_TenantGame_Insert\";");
        migrationBuilder.Sql($"DROP TRIGGER IF EXISTS \"TR_{table}_TenantGame_Update\";");
    }
}
