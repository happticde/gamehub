using GameHub.Core.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameHub.Core.Migrations.postgres;

/// <inheritdoc />
[DbContext(typeof(CoreContext))]
[Migration("20260718135046_AddOidcBackChannelLogoutPostgres")]
public sealed class AddOidcBackChannelLogoutPostgres : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        if (migrationBuilder.ActiveProvider != "Npgsql.EntityFrameworkCore.PostgreSQL")
        {
            return;
        }

        migrationBuilder.AddColumn<long>(
            name: "SessionVersion",
            table: "UserAccounts",
            type: "bigint",
            nullable: false,
            defaultValue: 0L);

        migrationBuilder.AddColumn<long>(
            name: "SessionVersion",
            table: "RefreshTokenSessions",
            type: "bigint",
            nullable: false,
            defaultValue: 0L);

        migrationBuilder.CreateTable(
            name: "ProcessedOidcLogoutTokens",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TokenHash = table.Column<string>(
                    type: "character varying(64)",
                    maxLength: 64,
                    nullable: false),
                ProcessedAtUtc = table.Column<DateTime>(
                    type: "timestamp with time zone",
                    nullable: false),
                ExpiresAtUtc = table.Column<DateTime>(
                    type: "timestamp with time zone",
                    nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ProcessedOidcLogoutTokens", row => row.Id);
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
        if (migrationBuilder.ActiveProvider != "Npgsql.EntityFrameworkCore.PostgreSQL")
        {
            return;
        }

        migrationBuilder.DropTable(name: "ProcessedOidcLogoutTokens");
        migrationBuilder.DropColumn(name: "SessionVersion", table: "UserAccounts");
        migrationBuilder.DropColumn(name: "SessionVersion", table: "RefreshTokenSessions");
    }
}
