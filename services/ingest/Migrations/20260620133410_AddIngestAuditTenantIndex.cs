using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameHub.Ingest.Migrations
{
    /// <inheritdoc />
    public partial class AddIngestAuditTenantIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_IngestAuditLogs_TenantId",
                table: "IngestAuditLogs",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_IngestAuditLogs_TenantId",
                table: "IngestAuditLogs");
        }
    }
}
