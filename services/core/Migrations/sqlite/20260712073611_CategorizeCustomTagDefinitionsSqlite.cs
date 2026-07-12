using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameHub.Core.Migrations.sqlite
{
    /// <inheritdoc />
    public partial class CategorizeCustomTagDefinitionsSqlite : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider != "Microsoft.EntityFrameworkCore.Sqlite")
            {
                return;
            }

            migrationBuilder.AddColumn<bool>(
                name: "IsCustomDefinition",
                table: "Tags",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // The previous schema recorded only assignment origin. Preserve every definition that
            // can still be proven custom; unused legacy definitions cannot be distinguished.
            migrationBuilder.Sql("""
                UPDATE "Tags"
                SET "IsCustomDefinition" = 1
                WHERE "Id" IN (
                    SELECT DISTINCT "TagsId"
                    FROM "TagUnifiedGame"
                    WHERE "IsCustom" = 1
                );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider != "Microsoft.EntityFrameworkCore.Sqlite")
            {
                return;
            }

            migrationBuilder.DropColumn(
                name: "IsCustomDefinition",
                table: "Tags");
        }
    }
}
