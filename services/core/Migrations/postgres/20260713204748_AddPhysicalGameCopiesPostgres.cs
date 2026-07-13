using GameHub.Core.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace GameHub.Core.Migrations.postgres;

/// <inheritdoc />
[DbContext(typeof(CoreContext))]
[Migration("20260713204748_AddPhysicalGameCopiesPostgres")]
public partial class AddPhysicalGameCopiesPostgres : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        if (migrationBuilder.ActiveProvider != "Npgsql.EntityFrameworkCore.PostgreSQL")
        {
            return;
        }

        migrationBuilder.CreateTable(
            name: "PhysicalGameCopies",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation(
                        "Npgsql:ValueGenerationStrategy",
                        NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                TenantId = table.Column<Guid>(
                    type: "uuid",
                    nullable: false,
                    defaultValue: TenantDefaults.DefaultTenantId),
                UnifiedGameId = table.Column<int>(type: "integer", nullable: false),
                Platform = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PhysicalGameCopies", row => row.Id);
                table.CheckConstraint(
                    "CK_PhysicalGameCopies_Platform",
                    "\"Platform\" IN (0, 1, 2)");
                table.ForeignKey(
                    name: "FK_PhysicalGameCopies_Tenants_TenantId",
                    column: row => row.TenantId,
                    principalTable: "Tenants",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_PhysicalGameCopies_UnifiedGames_TenantId_UnifiedGameId",
                    columns: row => new { row.TenantId, row.UnifiedGameId },
                    principalTable: "UnifiedGames",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_PhysicalGameCopies_TenantId_UnifiedGameId_Platform",
            table: "PhysicalGameCopies",
            columns: new[] { "TenantId", "UnifiedGameId", "Platform" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        if (migrationBuilder.ActiveProvider != "Npgsql.EntityFrameworkCore.PostgreSQL")
        {
            return;
        }

        migrationBuilder.DropTable(name: "PhysicalGameCopies");
    }
}
