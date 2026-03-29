using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameHub.Core.Migrations.sqlite
{
    /// <inheritdoc />
    public partial class AddActivityLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider != "Microsoft.EntityFrameworkCore.Sqlite")
            {
                return;
            }

            migrationBuilder.CreateTable(
                name: "ActivityLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EventType = table.Column<string>(type: "TEXT", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: false),
                    Details = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActivityLogs", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider != "Microsoft.EntityFrameworkCore.Sqlite")
            {
                return;
            }

            migrationBuilder.DropTable(
                name: "ActivityLogs");
        }
    }
}
