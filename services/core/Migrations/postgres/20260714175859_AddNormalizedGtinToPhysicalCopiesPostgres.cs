using GameHub.Core.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameHub.Core.Migrations.postgres;

/// <inheritdoc />
[DbContext(typeof(CoreContext))]
[Migration("20260714175859_AddNormalizedGtinToPhysicalCopiesPostgres")]
public sealed class AddNormalizedGtinToPhysicalCopiesPostgres : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        if (migrationBuilder.ActiveProvider != "Npgsql.EntityFrameworkCore.PostgreSQL")
        {
            return;
        }

        migrationBuilder.AddColumn<string>(
            name: "NormalizedGtin",
            table: "PhysicalGameCopies",
            type: "character varying(14)",
            maxLength: 14,
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_PhysicalGameCopies_TenantId_NormalizedGtin",
            table: "PhysicalGameCopies",
            columns: new[] { "TenantId", "NormalizedGtin" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        if (migrationBuilder.ActiveProvider != "Npgsql.EntityFrameworkCore.PostgreSQL")
        {
            return;
        }

        migrationBuilder.DropIndex(
            name: "IX_PhysicalGameCopies_TenantId_NormalizedGtin",
            table: "PhysicalGameCopies");

        migrationBuilder.DropColumn(
            name: "NormalizedGtin",
            table: "PhysicalGameCopies");
    }
}
