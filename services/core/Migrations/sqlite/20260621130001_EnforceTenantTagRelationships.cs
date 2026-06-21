using GameHub.Core.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameHub.Core.Migrations.sqlite;

/// <inheritdoc />
[DbContext(typeof(CoreContext))]
[Migration("20260621130001_EnforceTenantTagRelationships")]
public partial class EnforceTenantTagRelationships : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        if (migrationBuilder.ActiveProvider != "Microsoft.EntityFrameworkCore.Sqlite")
        {
            return;
        }

        migrationBuilder.CreateIndex(
            name: "UX_Tags_TenantId_Id",
            table: "Tags",
            columns: new[] { "TenantId", "Id" },
            unique: true);

        // Rebuild the join table because SQLite cannot add the required composite foreign keys.
        // Cross-tenant legacy rows retain their tag name through a tenant-local tag copy.
        migrationBuilder.Sql("""
            INSERT INTO "Tags" ("TenantId", "Name")
            SELECT DISTINCT games."TenantId", source_tags."Name"
            FROM "TagUnifiedGame" assignments
            INNER JOIN "UnifiedGames" games ON games."Id" = assignments."GamesId"
            INNER JOIN "Tags" source_tags ON source_tags."Id" = assignments."TagsId"
            WHERE source_tags."TenantId" <> games."TenantId"
            ON CONFLICT ("TenantId", "Name") DO NOTHING;

            CREATE TABLE "TagUnifiedGame_TenantSafe" (
                "TenantId" TEXT NOT NULL,
                "GamesId" INTEGER NOT NULL,
                "TagsId" INTEGER NOT NULL,
                CONSTRAINT "PK_TagUnifiedGame" PRIMARY KEY ("TenantId", "GamesId", "TagsId"),
                CONSTRAINT "FK_TagUnifiedGame_Tenants_TenantId"
                    FOREIGN KEY ("TenantId") REFERENCES "Tenants" ("Id") ON DELETE RESTRICT,
                CONSTRAINT "FK_TagUnifiedGame_Tags_TenantId_TagsId"
                    FOREIGN KEY ("TenantId", "TagsId") REFERENCES "Tags" ("TenantId", "Id") ON DELETE CASCADE,
                CONSTRAINT "FK_TagUnifiedGame_UnifiedGames_TenantId_GamesId"
                    FOREIGN KEY ("TenantId", "GamesId") REFERENCES "UnifiedGames" ("TenantId", "Id") ON DELETE CASCADE
            );

            INSERT INTO "TagUnifiedGame_TenantSafe" ("TenantId", "GamesId", "TagsId")
            SELECT DISTINCT games."TenantId", assignments."GamesId", destination_tags."Id"
            FROM "TagUnifiedGame" assignments
            INNER JOIN "UnifiedGames" games ON games."Id" = assignments."GamesId"
            INNER JOIN "Tags" source_tags ON source_tags."Id" = assignments."TagsId"
            INNER JOIN "Tags" destination_tags
                ON destination_tags."TenantId" = games."TenantId"
               AND destination_tags."Name" = source_tags."Name";

            DROP TABLE "TagUnifiedGame";
            ALTER TABLE "TagUnifiedGame_TenantSafe" RENAME TO "TagUnifiedGame";
            CREATE INDEX "IX_TagUnifiedGame_TenantId_TagsId"
                ON "TagUnifiedGame" ("TenantId", "TagsId");
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        if (migrationBuilder.ActiveProvider != "Microsoft.EntityFrameworkCore.Sqlite")
        {
            return;
        }

        migrationBuilder.Sql("""
            CREATE TABLE "TagUnifiedGame_Legacy" (
                "GamesId" INTEGER NOT NULL,
                "TagsId" INTEGER NOT NULL,
                CONSTRAINT "PK_TagUnifiedGame" PRIMARY KEY ("GamesId", "TagsId"),
                CONSTRAINT "FK_TagUnifiedGame_Tags_TagsId"
                    FOREIGN KEY ("TagsId") REFERENCES "Tags" ("Id") ON DELETE CASCADE,
                CONSTRAINT "FK_TagUnifiedGame_UnifiedGames_GamesId"
                    FOREIGN KEY ("GamesId") REFERENCES "UnifiedGames" ("Id") ON DELETE CASCADE
            );

            INSERT INTO "TagUnifiedGame_Legacy" ("GamesId", "TagsId")
            SELECT DISTINCT "GamesId", "TagsId" FROM "TagUnifiedGame";

            DROP TABLE "TagUnifiedGame";
            ALTER TABLE "TagUnifiedGame_Legacy" RENAME TO "TagUnifiedGame";
            CREATE INDEX "IX_TagUnifiedGame_TagsId" ON "TagUnifiedGame" ("TagsId");
            """);

        migrationBuilder.DropIndex(
            name: "UX_Tags_TenantId_Id",
            table: "Tags");
    }
}
