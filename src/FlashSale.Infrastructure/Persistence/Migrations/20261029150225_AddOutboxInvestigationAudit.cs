using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlashSale.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOutboxInvestigationAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OutboxInvestigationAudits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    Verdict = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Recommendation = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    EvidenceHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    AssessedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxInvestigationAudits", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxInvestigationAudits_CreatedAt",
                table: "OutboxInvestigationAudits",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxInvestigationAudits_MessageId",
                table: "OutboxInvestigationAudits",
                column: "MessageId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OutboxInvestigationAudits");
        }
    }
}
