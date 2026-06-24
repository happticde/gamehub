using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameHub.Core.Migrations.sqlite;

/// <inheritdoc />
public partial class AddExternalOidcAccountsSqlite : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // This migration was scaffolded with the SQLite provider. Unrelated type-only operations
        // caused by the historically shared PostgreSQL-shaped snapshot were removed during the
        // required review; the generated account/tombstone delta remains unchanged.
        if (migrationBuilder.ActiveProvider != "Microsoft.EntityFrameworkCore.Sqlite")
        {
            return;
        }

        migrationBuilder.DropIndex(
            name: "IX_UserAccounts_NormalizedEmail",
            table: "UserAccounts");

        migrationBuilder.AlterColumn<string>(
            name: "PasswordHash",
            table: "UserAccounts",
            type: "TEXT",
            maxLength: 1024,
            nullable: true,
            oldClrType: typeof(string),
            oldType: "TEXT",
            oldMaxLength: 1024);

        migrationBuilder.AlterColumn<string>(
            name: "NormalizedEmail",
            table: "UserAccounts",
            type: "TEXT",
            maxLength: 320,
            nullable: true,
            oldClrType: typeof(string),
            oldType: "TEXT",
            oldMaxLength: 320);

        migrationBuilder.AlterColumn<string>(
            name: "Email",
            table: "UserAccounts",
            type: "TEXT",
            maxLength: 320,
            nullable: true,
            oldClrType: typeof(string),
            oldType: "TEXT",
            oldMaxLength: 320);

        migrationBuilder.AddColumn<int>(
            name: "AccountType",
            table: "UserAccounts",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.CreateTable(
            name: "DeletedExternalIdentities",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Issuer = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                Subject = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                DeletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_DeletedExternalIdentities", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_UserAccounts_NormalizedEmail",
            table: "UserAccounts",
            column: "NormalizedEmail",
            unique: true,
            filter: "\"AccountType\" = 0 AND \"NormalizedEmail\" IS NOT NULL");

        migrationBuilder.AddCheckConstraint(
            name: "CK_UserAccounts_AccountTypeCredentials",
            table: "UserAccounts",
            sql: "(\"AccountType\" = 0 AND \"Email\" IS NOT NULL AND length(\"Email\") > 0 AND \"NormalizedEmail\" IS NOT NULL AND length(\"NormalizedEmail\") > 0 AND \"PasswordHash\" IS NOT NULL AND length(\"PasswordHash\") > 0) OR (\"AccountType\" = 1 AND \"NormalizedEmail\" IS NULL AND \"PasswordHash\" IS NULL)");

        migrationBuilder.CreateIndex(
            name: "IX_DeletedExternalIdentities_Issuer_Subject",
            table: "DeletedExternalIdentities",
            columns: new[] { "Issuer", "Subject" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        if (migrationBuilder.ActiveProvider != "Microsoft.EntityFrameworkCore.Sqlite")
        {
            return;
        }

        migrationBuilder.DropTable(name: "DeletedExternalIdentities");
        migrationBuilder.DropIndex(
            name: "IX_UserAccounts_NormalizedEmail",
            table: "UserAccounts");
        migrationBuilder.DropCheckConstraint(
            name: "CK_UserAccounts_AccountTypeCredentials",
            table: "UserAccounts");
        migrationBuilder.DropColumn(
            name: "AccountType",
            table: "UserAccounts");

        migrationBuilder.AlterColumn<string>(
            name: "PasswordHash",
            table: "UserAccounts",
            type: "TEXT",
            maxLength: 1024,
            nullable: false,
            defaultValue: "",
            oldClrType: typeof(string),
            oldType: "TEXT",
            oldMaxLength: 1024,
            oldNullable: true);
        migrationBuilder.AlterColumn<string>(
            name: "NormalizedEmail",
            table: "UserAccounts",
            type: "TEXT",
            maxLength: 320,
            nullable: false,
            defaultValue: "",
            oldClrType: typeof(string),
            oldType: "TEXT",
            oldMaxLength: 320,
            oldNullable: true);
        migrationBuilder.AlterColumn<string>(
            name: "Email",
            table: "UserAccounts",
            type: "TEXT",
            maxLength: 320,
            nullable: false,
            defaultValue: "",
            oldClrType: typeof(string),
            oldType: "TEXT",
            oldMaxLength: 320,
            oldNullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_UserAccounts_NormalizedEmail",
            table: "UserAccounts",
            column: "NormalizedEmail",
            unique: true);
    }
}
