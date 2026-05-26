using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCapitalisedPaymentRegistry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CapitalisedPaymentDecisions",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RequestId = table.Column<long>(type: "bigint", nullable: false),
                    DecisionStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ComputedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EffectiveAgeYears = table.Column<decimal>(type: "numeric(6,2)", precision: 6, scale: 2, nullable: false),
                    LifeExpectancyMonths = table.Column<int>(type: "integer", nullable: false),
                    EffectiveDiscountMonthly = table.Column<decimal>(type: "numeric(12,8)", precision: 12, scale: 8, nullable: false),
                    CapitalisedAmountMdl = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    ComputationBreakdownJson = table.Column<string>(type: "character varying(32768)", maxLength: 32768, nullable: false),
                    ApprovedByUserId = table.Column<int>(type: "integer", nullable: true),
                    RejectionReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CapitalisedPaymentDecisions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CapitalisedPaymentRequests",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RequestNumber = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    BeneficiaryIdnp = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    BeneficiaryIdnpHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    BeneficiaryBirthDate = table.Column<DateOnly>(type: "date", nullable: false),
                    BeneficiarySex = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    LiquidatedDebtorIdno = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    LiquidatedDebtorIdnoHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    LiquidatedDebtorName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ObligationKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    MonthlyAmountMdl = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    ObligationStartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ObligationEndDate = table.Column<DateOnly>(type: "date", nullable: true),
                    ValuationDate = table.Column<DateOnly>(type: "date", nullable: false),
                    LegalDiscountRatePercent = table.Column<decimal>(type: "numeric(8,4)", precision: 8, scale: 4, nullable: false),
                    RegisteredByUserId = table.Column<int>(type: "integer", nullable: false),
                    CancellationReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CapitalisedPaymentRequests", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CapitalisedPaymentDecisions_ComputedAtUtc",
                schema: "cnas",
                table: "CapitalisedPaymentDecisions",
                column: "ComputedAtUtc",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_CapitalisedPaymentDecisions_CreatedAtUtc",
                schema: "cnas",
                table: "CapitalisedPaymentDecisions",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_CapitalisedPaymentDecisions_IsActive",
                schema: "cnas",
                table: "CapitalisedPaymentDecisions",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_CapitalisedPaymentDecisions_RequestId",
                schema: "cnas",
                table: "CapitalisedPaymentDecisions",
                column: "RequestId");

            migrationBuilder.CreateIndex(
                name: "IX_CapitalisedPaymentRequests_Beneficiary_Debtor_Status",
                schema: "cnas",
                table: "CapitalisedPaymentRequests",
                columns: new[] { "BeneficiaryIdnpHash", "LiquidatedDebtorIdnoHash", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_CapitalisedPaymentRequests_CreatedAtUtc",
                schema: "cnas",
                table: "CapitalisedPaymentRequests",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_CapitalisedPaymentRequests_IsActive",
                schema: "cnas",
                table: "CapitalisedPaymentRequests",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_CapitalisedPaymentRequests_Status_Kind",
                schema: "cnas",
                table: "CapitalisedPaymentRequests",
                columns: new[] { "Status", "ObligationKind" });

            migrationBuilder.CreateIndex(
                name: "UX_CapitalisedPaymentRequests_RequestNumber",
                schema: "cnas",
                table: "CapitalisedPaymentRequests",
                column: "RequestNumber",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CapitalisedPaymentDecisions",
                schema: "cnas");

            migrationBuilder.DropTable(
                name: "CapitalisedPaymentRequests",
                schema: "cnas");
        }
    }
}
