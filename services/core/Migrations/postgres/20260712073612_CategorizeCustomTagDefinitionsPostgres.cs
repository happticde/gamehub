using GameHub.Core.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameHub.Core.Migrations.postgres;

/// <inheritdoc />
[DbContext(typeof(CoreContext))]
[Migration("20260712073612_CategorizeCustomTagDefinitionsPostgres")]
public partial class CategorizeCustomTagDefinitionsPostgres : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        if (migrationBuilder.ActiveProvider != "Npgsql.EntityFrameworkCore.PostgreSQL")
        {
            return;
        }

        migrationBuilder.AddColumn<bool>(
            name: "IsCustomDefinition",
            table: "Tags",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        // The previous schema recorded only assignment origin. Preserve every definition that
        // can still be proven custom; unused legacy definitions cannot be distinguished.
        migrationBuilder.Sql("""
            UPDATE "Tags"
            SET "IsCustomDefinition" = TRUE
            WHERE "Id" IN (
                SELECT DISTINCT "TagsId"
                FROM "TagUnifiedGame"
                WHERE "IsCustom" = TRUE
            );
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        if (migrationBuilder.ActiveProvider != "Npgsql.EntityFrameworkCore.PostgreSQL")
        {
            return;
        }

        migrationBuilder.DropColumn(
            name: "IsCustomDefinition",
            table: "Tags");
    }
}
