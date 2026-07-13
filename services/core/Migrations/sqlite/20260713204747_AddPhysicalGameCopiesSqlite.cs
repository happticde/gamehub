using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameHub.Core.Migrations.sqlite
{
    /// <inheritdoc />
    public partial class AddPhysicalGameCopiesSqlite : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider != "Microsoft.EntityFrameworkCore.Sqlite")
            {
                return;
            }

            migrationBuilder.CreateTable(
                name: "PhysicalGameCopies",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false, defaultValue: new Guid("00000000-0000-0000-0000-000000000001")),
                    UnifiedGameId = table.Column<int>(type: "INTEGER", nullable: false),
                    Platform = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PhysicalGameCopies", x => x.Id);
                    table.CheckConstraint("CK_PhysicalGameCopies_Platform", "\"Platform\" IN (0, 1, 2)");
                    table.ForeignKey(
                        name: "FK_PhysicalGameCopies_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PhysicalGameCopies_UnifiedGames_TenantId_UnifiedGameId",
                        columns: x => new { x.TenantId, x.UnifiedGameId },
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
            if (migrationBuilder.ActiveProvider != "Microsoft.EntityFrameworkCore.Sqlite")
            {
                return;
            }

            migrationBuilder.DropTable(
                name: "PhysicalGameCopies");
        }
    }
}
