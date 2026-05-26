using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddClaimsAndPayments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClaimPayments",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ClaimId = table.Column<long>(type: "bigint", nullable: false),
                    PaidDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    PaymentReference = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    TreasuryPaymentReceiptId = table.Column<long>(type: "bigint", nullable: true),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClaimPayments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Claims",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ContributorId = table.Column<long>(type: "bigint", nullable: false),
                    ClaimNumber = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RelatedMonth = table.Column<DateOnly>(type: "date", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    PrincipalAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    PaidAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    RemainingAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    OpenedDate = table.Column<DateOnly>(type: "date", nullable: false),
                    DueDate = table.Column<DateOnly>(type: "date", nullable: true),
                    SettledDate = table.Column<DateOnly>(type: "date", nullable: true),
                    CancelledDate = table.Column<DateOnly>(type: "date", nullable: true),
                    CancelReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    RelatedDocumentReference = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Claims", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClaimPayments_Claim_PaidDate",
                schema: "cnas",
                table: "ClaimPayments",
                columns: new[] { "ClaimId", "PaidDate" });

            migrationBuilder.CreateIndex(
                name: "IX_ClaimPayments_CreatedAtUtc",
                schema: "cnas",
                table: "ClaimPayments",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ClaimPayments_IsActive",
                schema: "cnas",
                table: "ClaimPayments",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "UX_ClaimPayments_Claim_PaymentReference",
                schema: "cnas",
                table: "ClaimPayments",
                columns: new[] { "ClaimId", "PaymentReference" },
                unique: true,
                filter: "\"PaymentReference\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Claims_Contributor_Status",
                schema: "cnas",
                table: "Claims",
                columns: new[] { "ContributorId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Claims_CreatedAtUtc",
                schema: "cnas",
                table: "Claims",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Claims_IsActive",
                schema: "cnas",
                table: "Claims",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "UX_Claims_ClaimNumber",
                schema: "cnas",
                table: "Claims",
                column: "ClaimNumber",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClaimPayments",
                schema: "cnas");

            migrationBuilder.DropTable(
                name: "Claims",
                schema: "cnas");
        }
    }
}
