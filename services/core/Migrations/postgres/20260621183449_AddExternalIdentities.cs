using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameHub.Core.Migrations.postgres
{
    /// <inheritdoc />
    public partial class AddExternalIdentities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Core uses one ordered migration chain for both configured providers. EF scaffolded
            // the relationship from the PostgreSQL model; select only physical column types here
            // so SQLite and PostgreSQL receive the same keys and constraints without data loss.
            var postgres = migrationBuilder.ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL";
            var guidType = postgres ? "uuid" : "TEXT";
            var issuerType = postgres ? "character varying(2048)" : "TEXT";
            var subjectType = postgres ? "character varying(255)" : "TEXT";
            var timestampType = postgres ? "timestamp with time zone" : "TEXT";

            migrationBuilder.CreateIndex(
                name: "UX_UserAccounts_TenantId_Id",
                table: "UserAccounts",
                columns: new[] { "TenantId", "Id" },
                unique: true);

            migrationBuilder.CreateTable(
                name: "ExternalIdentities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: guidType, nullable: false),
                    Issuer = table.Column<string>(type: issuerType, maxLength: 2048, nullable: false),
                    Subject = table.Column<string>(type: subjectType, maxLength: 255, nullable: false),
                    UserAccountId = table.Column<Guid>(type: guidType, nullable: false),
                    TenantId = table.Column<Guid>(type: guidType, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: timestampType, nullable: false),
                    LastLoginAtUtc = table.Column<DateTime>(type: timestampType, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalIdentities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExternalIdentities_UserAccounts_TenantId_UserAccountId",
                        columns: x => new { x.TenantId, x.UserAccountId },
                        principalTable: "UserAccounts",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExternalIdentities_Issuer_Subject",
                table: "ExternalIdentities",
                columns: new[] { "Issuer", "Subject" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExternalIdentities_TenantId_UserAccountId",
                table: "ExternalIdentities",
                columns: new[] { "TenantId", "UserAccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_ExternalIdentities_UserAccountId_Issuer",
                table: "ExternalIdentities",
                columns: new[] { "UserAccountId", "Issuer" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExternalIdentities");

            migrationBuilder.DropIndex(
                name: "UX_UserAccounts_TenantId_Id",
                table: "UserAccounts");
        }
    }
}
