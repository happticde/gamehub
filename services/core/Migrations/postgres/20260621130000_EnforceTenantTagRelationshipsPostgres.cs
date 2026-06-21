using GameHub.Core.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameHub.Core.Migrations.postgres;

/// <inheritdoc />
[DbContext(typeof(CoreContext))]
[Migration("20260621130000_EnforceTenantTagRelationshipsPostgres")]
public partial class EnforceTenantTagRelationshipsPostgres : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        if (migrationBuilder.ActiveProvider != "Npgsql.EntityFrameworkCore.PostgreSQL")
        {
            return;
        }

        migrationBuilder.DropForeignKey(
            name: "FK_TagUnifiedGame_Tags_TagsId",
            table: "TagUnifiedGame");
        migrationBuilder.DropForeignKey(
            name: "FK_TagUnifiedGame_UnifiedGames_GamesId",
            table: "TagUnifiedGame");
        migrationBuilder.DropPrimaryKey(
            name: "PK_TagUnifiedGame",
            table: "TagUnifiedGame");
        migrationBuilder.DropIndex(
            name: "IX_TagUnifiedGame_TagsId",
            table: "TagUnifiedGame");

        migrationBuilder.AddColumn<Guid>(
            name: "TenantId",
            table: "TagUnifiedGame",
            type: "uuid",
            nullable: true);

        // A relation created before this migration may point at a tag owned by another tenant.
        // Keep its semantic assignment by creating or reusing the same tag name for the game owner.
        migrationBuilder.Sql("""
            INSERT INTO "Tags" ("TenantId", "Name")
            SELECT DISTINCT games."TenantId", source_tags."Name"
            FROM "TagUnifiedGame" assignments
            INNER JOIN "UnifiedGames" games ON games."Id" = assignments."GamesId"
            INNER JOIN "Tags" source_tags ON source_tags."Id" = assignments."TagsId"
            WHERE source_tags."TenantId" <> games."TenantId"
            ON CONFLICT ("TenantId", "Name") DO NOTHING;
            """);
        migrationBuilder.Sql("""
            UPDATE "TagUnifiedGame" assignments
            SET "TenantId" = games."TenantId",
                "TagsId" = destination_tags."Id"
            FROM "UnifiedGames" games,
                 "Tags" source_tags,
                 "Tags" destination_tags
            WHERE games."Id" = assignments."GamesId"
              AND source_tags."Id" = assignments."TagsId"
              AND destination_tags."TenantId" = games."TenantId"
              AND destination_tags."Name" = source_tags."Name";
            """);
        migrationBuilder.Sql("""
            DELETE FROM "TagUnifiedGame" duplicate
            USING "TagUnifiedGame" retained
            WHERE duplicate.ctid < retained.ctid
              AND duplicate."TenantId" = retained."TenantId"
              AND duplicate."GamesId" = retained."GamesId"
              AND duplicate."TagsId" = retained."TagsId";
            """);

        migrationBuilder.AlterColumn<Guid>(
            name: "TenantId",
            table: "TagUnifiedGame",
            type: "uuid",
            nullable: false,
            oldClrType: typeof(Guid),
            oldType: "uuid",
            oldNullable: true);

        migrationBuilder.AddUniqueConstraint(
            name: "AK_Tags_TenantId_Id",
            table: "Tags",
            columns: new[] { "TenantId", "Id" });
        migrationBuilder.AddPrimaryKey(
            name: "PK_TagUnifiedGame",
            table: "TagUnifiedGame",
            columns: new[] { "TenantId", "GamesId", "TagsId" });
        migrationBuilder.CreateIndex(
            name: "IX_TagUnifiedGame_TenantId_TagsId",
            table: "TagUnifiedGame",
            columns: new[] { "TenantId", "TagsId" });
        migrationBuilder.AddForeignKey(
            name: "FK_TagUnifiedGame_Tenants_TenantId",
            table: "TagUnifiedGame",
            column: "TenantId",
            principalTable: "Tenants",
            principalColumn: "Id",
            onDelete: ReferentialAction.Restrict);
        migrationBuilder.AddForeignKey(
            name: "FK_TagUnifiedGame_Tags_TenantId_TagsId",
            table: "TagUnifiedGame",
            columns: new[] { "TenantId", "TagsId" },
            principalTable: "Tags",
            principalColumns: new[] { "TenantId", "Id" },
            onDelete: ReferentialAction.Cascade);
        migrationBuilder.AddForeignKey(
            name: "FK_TagUnifiedGame_UnifiedGames_TenantId_GamesId",
            table: "TagUnifiedGame",
            columns: new[] { "TenantId", "GamesId" },
            principalTable: "UnifiedGames",
            principalColumns: new[] { "TenantId", "Id" },
            onDelete: ReferentialAction.Cascade);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        if (migrationBuilder.ActiveProvider != "Npgsql.EntityFrameworkCore.PostgreSQL")
        {
            return;
        }

        migrationBuilder.DropForeignKey(
            name: "FK_TagUnifiedGame_Tenants_TenantId",
            table: "TagUnifiedGame");
        migrationBuilder.DropForeignKey(
            name: "FK_TagUnifiedGame_Tags_TenantId_TagsId",
            table: "TagUnifiedGame");
        migrationBuilder.DropForeignKey(
            name: "FK_TagUnifiedGame_UnifiedGames_TenantId_GamesId",
            table: "TagUnifiedGame");
        migrationBuilder.DropPrimaryKey(
            name: "PK_TagUnifiedGame",
            table: "TagUnifiedGame");
        migrationBuilder.DropIndex(
            name: "IX_TagUnifiedGame_TenantId_TagsId",
            table: "TagUnifiedGame");
        migrationBuilder.DropUniqueConstraint(
            name: "AK_Tags_TenantId_Id",
            table: "Tags");
        migrationBuilder.DropColumn(
            name: "TenantId",
            table: "TagUnifiedGame");

        migrationBuilder.AddPrimaryKey(
            name: "PK_TagUnifiedGame",
            table: "TagUnifiedGame",
            columns: new[] { "GamesId", "TagsId" });
        migrationBuilder.CreateIndex(
            name: "IX_TagUnifiedGame_TagsId",
            table: "TagUnifiedGame",
            column: "TagsId");
        migrationBuilder.AddForeignKey(
            name: "FK_TagUnifiedGame_Tags_TagsId",
            table: "TagUnifiedGame",
            column: "TagsId",
            principalTable: "Tags",
            principalColumn: "Id",
            onDelete: ReferentialAction.Cascade);
        migrationBuilder.AddForeignKey(
            name: "FK_TagUnifiedGame_UnifiedGames_GamesId",
            table: "TagUnifiedGame",
            column: "GamesId",
            principalTable: "UnifiedGames",
            principalColumn: "Id",
            onDelete: ReferentialAction.Cascade);
    }
}
