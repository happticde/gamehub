using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameHub.Core.Migrations.sqlite
{
    /// <inheritdoc />
    public partial class AddOidcBackChannelLogoutSqlite : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider != "Microsoft.EntityFrameworkCore.Sqlite")
            {
                return;
            }

            migrationBuilder.AddColumn<long>(
                name: "SessionVersion",
                table: "UserAccounts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "SessionVersion",
                table: "RefreshTokenSessions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "ProcessedOidcLogoutTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ProcessedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProcessedOidcLogoutTokens", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProcessedOidcLogoutTokens_ExpiresAtUtc",
                table: "ProcessedOidcLogoutTokens",
                column: "ExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ProcessedOidcLogoutTokens_TokenHash",
                table: "ProcessedOidcLogoutTokens",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider != "Microsoft.EntityFrameworkCore.Sqlite")
            {
                return;
            }

            migrationBuilder.DropTable(
                name: "ProcessedOidcLogoutTokens");

            migrationBuilder.DropColumn(
                name: "SessionVersion",
                table: "UserAccounts");

            migrationBuilder.DropColumn(
                name: "SessionVersion",
                table: "RefreshTokenSessions");
        }
    }
}
