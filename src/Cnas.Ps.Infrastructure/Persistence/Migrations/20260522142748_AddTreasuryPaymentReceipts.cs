using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTreasuryPaymentReceipts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TreasuryPaymentReceipts",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TreasuryReferenceNumber = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ReceiptDate = table.Column<DateOnly>(type: "date", nullable: false),
                    PayerContributorId = table.Column<long>(type: "bigint", nullable: false),
                    ReportingMonth = table.Column<DateOnly>(type: "date", nullable: false),
                    AmountReceived = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    DistributionStatus = table.Column<int>(type: "integer", nullable: false),
                    DistributedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DistributionFailureReason = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    UndistributedRemainderAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TreasuryPaymentReceipts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TreasuryPaymentReceipts_CreatedAtUtc",
                schema: "cnas",
                table: "TreasuryPaymentReceipts",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_TreasuryPaymentReceipts_IsActive",
                schema: "cnas",
                table: "TreasuryPaymentReceipts",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_TreasuryPaymentReceipts_Payer_Month",
                schema: "cnas",
                table: "TreasuryPaymentReceipts",
                columns: new[] { "PayerContributorId", "ReportingMonth" });

            migrationBuilder.CreateIndex(
                name: "IX_TreasuryPaymentReceipts_Status_Date",
                schema: "cnas",
                table: "TreasuryPaymentReceipts",
                columns: new[] { "DistributionStatus", "ReceiptDate" });

            migrationBuilder.CreateIndex(
                name: "UX_TreasuryPaymentReceipts_Reference",
                schema: "cnas",
                table: "TreasuryPaymentReceipts",
                column: "TreasuryReferenceNumber",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TreasuryPaymentReceipts",
                schema: "cnas");
        }
    }
}
