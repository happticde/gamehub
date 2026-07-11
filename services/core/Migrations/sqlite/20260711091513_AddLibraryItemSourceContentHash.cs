using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameHub.Core.Migrations.sqlite
{
    /// <inheritdoc />
    public partial class AddLibraryItemSourceContentHash : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider != "Microsoft.EntityFrameworkCore.Sqlite")
            {
                return;
            }

            migrationBuilder.AddColumn<string>(
                name: "SourceContentHash",
                table: "LibraryItems",
                type: "TEXT",
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
            if (migrationBuilder.ActiveProvider != "Microsoft.EntityFrameworkCore.Sqlite")
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
}
