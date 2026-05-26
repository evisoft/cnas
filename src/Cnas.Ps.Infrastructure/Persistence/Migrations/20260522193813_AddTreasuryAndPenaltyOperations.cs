using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTreasuryAndPenaltyOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PenaltyRepaymentInstallments",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PenaltyRepaymentPlanId = table.Column<long>(type: "bigint", nullable: false),
                    InstallmentNumber = table.Column<int>(type: "integer", nullable: false),
                    DueDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    PaidDate = table.Column<DateOnly>(type: "date", nullable: true),
                    PaidAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    IsPaid = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PenaltyRepaymentInstallments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PenaltyRepaymentPlans",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LatePaymentPenaltyId = table.Column<long>(type: "bigint", nullable: false),
                    InstallmentCount = table.Column<int>(type: "integer", nullable: false),
                    InstallmentAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    FirstInstallmentDueDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    PaidInstallmentCount = table.Column<int>(type: "integer", nullable: false),
                    RemainingAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CancelledUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CancelReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PenaltyRepaymentPlans", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PenaltyRepaymentInstallments_CreatedAtUtc",
                schema: "cnas",
                table: "PenaltyRepaymentInstallments",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_PenaltyRepaymentInstallments_IsActive",
                schema: "cnas",
                table: "PenaltyRepaymentInstallments",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_PenaltyRepaymentInstallments_Plan_DueDate",
                schema: "cnas",
                table: "PenaltyRepaymentInstallments",
                columns: new[] { "PenaltyRepaymentPlanId", "DueDate" });

            migrationBuilder.CreateIndex(
                name: "UX_PenaltyRepaymentInstallments_Plan_Number",
                schema: "cnas",
                table: "PenaltyRepaymentInstallments",
                columns: new[] { "PenaltyRepaymentPlanId", "InstallmentNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PenaltyRepaymentPlans_CreatedAtUtc",
                schema: "cnas",
                table: "PenaltyRepaymentPlans",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_PenaltyRepaymentPlans_IsActive",
                schema: "cnas",
                table: "PenaltyRepaymentPlans",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_PenaltyRepaymentPlans_Status",
                schema: "cnas",
                table: "PenaltyRepaymentPlans",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "UX_PenaltyRepaymentPlans_Penalty_Active",
                schema: "cnas",
                table: "PenaltyRepaymentPlans",
                column: "LatePaymentPenaltyId",
                unique: true,
                filter: "\"Status\" = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PenaltyRepaymentInstallments",
                schema: "cnas");

            migrationBuilder.DropTable(
                name: "PenaltyRepaymentPlans",
                schema: "cnas");
        }
    }
}
