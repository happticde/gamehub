using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameHub.Core.Migrations.postgres;

/// <inheritdoc />
public partial class AddLibraryItemSourceContentHashPostgres : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        if (migrationBuilder.ActiveProvider != "Npgsql.EntityFrameworkCore.PostgreSQL")
        {
            return;
        }

        migrationBuilder.AddColumn<string>(
            name: "SourceContentHash",
            table: "LibraryItems",
            type: "character varying(64)",
            maxLength: 64,
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_LibraryItems_TenantId_Platform_ExternalId",
            table: "LibraryItems",
            columns: new[] { "TenantId", "Platform", "ExternalId" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        if (migrationBuilder.ActiveProvider != "Npgsql.EntityFrameworkCore.PostgreSQL")
        {
            return;
        }

        migrationBuilder.DropIndex(
            name: "IX_LibraryItems_TenantId_Platform_ExternalId",
            table: "LibraryItems");

        migrationBuilder.DropColumn(
            name: "SourceContentHash",
            table: "LibraryItems");
    }
}
