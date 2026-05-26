using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDeclarationsAndMonthlyCalc : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Declarations",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ContributorId = table.Column<long>(type: "bigint", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    ReportingMonth = table.Column<DateOnly>(type: "date", nullable: false),
                    FiledAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReferenceNumber = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DeclaredContributionAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    AdjustedContributionAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    IsArchived = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Declarations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MonthlyContributionCalculations",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ContributorId = table.Column<long>(type: "bigint", nullable: false),
                    Month = table.Column<DateOnly>(type: "date", nullable: false),
                    TotalDeclared = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    TotalAdjusted = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    OverpaymentAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    UnderpaymentAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    DeclarationCount = table.Column<int>(type: "integer", nullable: false),
                    CalculatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MonthlyContributionCalculations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Declarations_Contributor_Month",
                schema: "cnas",
                table: "Declarations",
                columns: new[] { "ContributorId", "ReportingMonth" });

            migrationBuilder.CreateIndex(
                name: "IX_Declarations_CreatedAtUtc",
                schema: "cnas",
                table: "Declarations",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Declarations_IsActive",
                schema: "cnas",
                table: "Declarations",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_Declarations_Kind_Month",
                schema: "cnas",
                table: "Declarations",
                columns: new[] { "Kind", "ReportingMonth" });

            migrationBuilder.CreateIndex(
                name: "UX_Declarations_NaturalKey",
                schema: "cnas",
                table: "Declarations",
                columns: new[] { "ContributorId", "Kind", "ReportingMonth", "ReferenceNumber" },
                unique: true,
                filter: "\"ReferenceNumber\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_MonthlyContributionCalculations_CreatedAtUtc",
                schema: "cnas",
                table: "MonthlyContributionCalculations",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_MonthlyContributionCalculations_IsActive",
                schema: "cnas",
                table: "MonthlyContributionCalculations",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_MonthlyContributionCalculations_Month_Contributor",
                schema: "cnas",
                table: "MonthlyContributionCalculations",
                columns: new[] { "Month", "ContributorId" });

            migrationBuilder.CreateIndex(
                name: "UX_MonthlyContributionCalculations_NaturalKey",
                schema: "cnas",
                table: "MonthlyContributionCalculations",
                columns: new[] { "ContributorId", "Month" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Declarations",
                schema: "cnas");

            migrationBuilder.DropTable(
                name: "MonthlyContributionCalculations",
                schema: "cnas");
        }
    }
}
