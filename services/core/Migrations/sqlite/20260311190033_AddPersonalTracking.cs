using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameHub.Core.Migrations.sqlite
{
    /// <inheritdoc />
    public partial class AddPersonalTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsPlayStatusManual",
                table: "UnifiedGames",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "PlayStatus",
                table: "UnifiedGames",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "UserRating",
                table: "UnifiedGames",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsPlayStatusManual",
                table: "UnifiedGames");

            migrationBuilder.DropColumn(
                name: "PlayStatus",
                table: "UnifiedGames");

            migrationBuilder.DropColumn(
                name: "UserRating",
                table: "UnifiedGames");
        }
    }
}
