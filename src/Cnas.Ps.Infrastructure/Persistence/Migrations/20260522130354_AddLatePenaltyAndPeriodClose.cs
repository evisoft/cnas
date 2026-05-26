using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLatePenaltyAndPeriodClose : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LatePaymentPenalties",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ContributorId = table.Column<long>(type: "bigint", nullable: false),
                    Month = table.Column<DateOnly>(type: "date", nullable: false),
                    PrincipalAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    CalculatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DueDate = table.Column<DateOnly>(type: "date", nullable: false),
                    UpToDate = table.Column<DateOnly>(type: "date", nullable: false),
                    DaysLate = table.Column<int>(type: "integer", nullable: false),
                    DailyRatePercent = table.Column<decimal>(type: "numeric(9,6)", precision: 9, scale: 6, nullable: false),
                    PenaltyAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    IsWaived = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    WaiveReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LatePaymentPenalties", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ManagementPeriodCloses",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Month = table.Column<DateOnly>(type: "date", nullable: false),
                    ClosedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ClosedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    TotalDeclaredAcrossPayers = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    TotalPaidAcrossPayers = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    PayerCount = table.Column<int>(type: "integer", nullable: false),
                    DeclarationCount = table.Column<int>(type: "integer", nullable: false),
                    IsReopened = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    ReopenedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReopenedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    ReopenReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManagementPeriodCloses", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LatePaymentPenalties_CreatedAtUtc",
                schema: "cnas",
                table: "LatePaymentPenalties",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_LatePaymentPenalties_IsActive",
                schema: "cnas",
                table: "LatePaymentPenalties",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_LatePaymentPenalties_Month_Contributor",
                schema: "cnas",
                table: "LatePaymentPenalties",
                columns: new[] { "Month", "ContributorId" });

            migrationBuilder.CreateIndex(
                name: "UX_LatePaymentPenalties_NaturalKey",
                schema: "cnas",
                table: "LatePaymentPenalties",
                columns: new[] { "ContributorId", "Month", "UpToDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ManagementPeriodCloses_CreatedAtUtc",
                schema: "cnas",
                table: "ManagementPeriodCloses",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ManagementPeriodCloses_IsActive",
                schema: "cnas",
                table: "ManagementPeriodCloses",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "UX_ManagementPeriodCloses_Month",
                schema: "cnas",
                table: "ManagementPeriodCloses",
                column: "Month",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LatePaymentPenalties",
                schema: "cnas");

            migrationBuilder.DropTable(
                name: "ManagementPeriodCloses",
                schema: "cnas");
        }
    }
}
