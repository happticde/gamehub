using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameHub.Core.Migrations.sqlite
{
    /// <inheritdoc />
    public partial class AddCustomTagAssignmentsSqlite : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider != "Microsoft.EntityFrameworkCore.Sqlite")
            {
                return;
            }

            migrationBuilder.AddColumn<bool>(
                name: "IsCustom",
                table: "TagUnifiedGame",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider != "Microsoft.EntityFrameworkCore.Sqlite")
            {
                return;
            }

            migrationBuilder.DropColumn(
                name: "IsCustom",
                table: "TagUnifiedGame");
        }
    }
}
