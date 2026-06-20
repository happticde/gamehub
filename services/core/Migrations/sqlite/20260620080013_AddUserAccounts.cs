using GameHub.Core.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameHub.Core.Migrations.sqlite;

/// <inheritdoc />
[DbContext(typeof(CoreContext))]
[Migration("20260620080013_AddUserAccounts")]
public partial class AddUserAccounts : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        if (migrationBuilder.ActiveProvider != "Microsoft.EntityFrameworkCore.Sqlite")
        {
            return;
        }

        migrationBuilder.CreateTable(
            name: "UserAccounts",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                Email = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                NormalizedEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                DisplayName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                PasswordHash = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                LastLoginAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_UserAccounts", account => account.Id);
                table.ForeignKey(
                    name: "FK_UserAccounts_Tenants_TenantId",
                    column: account => account.TenantId,
                    principalTable: "Tenants",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_UserAccounts_NormalizedEmail",
            table: "UserAccounts",
            column: "NormalizedEmail",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_UserAccounts_TenantId",
            table: "UserAccounts",
            column: "TenantId",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        if (migrationBuilder.ActiveProvider != "Microsoft.EntityFrameworkCore.Sqlite")
        {
            return;
        }

        migrationBuilder.DropTable(name: "UserAccounts");
    }
}
