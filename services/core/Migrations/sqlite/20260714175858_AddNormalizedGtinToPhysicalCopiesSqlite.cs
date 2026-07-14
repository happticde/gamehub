using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameHub.Core.Migrations.sqlite
{
    /// <inheritdoc />
    public partial class AddNormalizedGtinToPhysicalCopiesSqlite : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider != "Microsoft.EntityFrameworkCore.Sqlite")
            {
                return;
            }

            migrationBuilder.AddColumn<string>(
                name: "NormalizedGtin",
                table: "PhysicalGameCopies",
                type: "TEXT",
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
            if (migrationBuilder.ActiveProvider != "Microsoft.EntityFrameworkCore.Sqlite")
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
}
