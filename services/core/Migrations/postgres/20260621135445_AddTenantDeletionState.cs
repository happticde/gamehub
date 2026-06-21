using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameHub.Core.Migrations.postgres
{
    /// <inheritdoc />
    public partial class AddTenantDeletionState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Core has one ordered migration chain for both configured providers. EF generated
            // this migration from the PostgreSQL snapshot; select only the physical timestamp
            // type here so the same additive, nullable column upgrades SQLite without rewriting
            // any existing tenant or library row.
            var timestampType = migrationBuilder.ActiveProvider ==
                "Npgsql.EntityFrameworkCore.PostgreSQL"
                    ? "timestamp with time zone"
                    : "TEXT";

            migrationBuilder.AddColumn<DateTime>(
                name: "DeletionRequestedAtUtc",
                table: "Tenants",
                type: timestampType,
                nullable: true);

            migrationBuilder.UpdateData(
                table: "Tenants",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0000-000000000001"),
                column: "DeletionRequestedAtUtc",
                value: null);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeletionRequestedAtUtc",
                table: "Tenants");
        }
    }
}
