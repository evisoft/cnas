using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBassRefundsAndPaymentCorrections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BassRefunds",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ContributorId = table.Column<long>(type: "bigint", nullable: false),
                    RelatedMonth = table.Column<DateOnly>(type: "date", nullable: false),
                    RefundAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    AuthorisationDocumentReference = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    RequestedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    ApprovedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    ApprovedDate = table.Column<DateOnly>(type: "date", nullable: true),
                    TreasuryDispatchReference = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IssuedDate = table.Column<DateOnly>(type: "date", nullable: true),
                    ConfirmedDate = table.Column<DateOnly>(type: "date", nullable: true),
                    CancelReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CancelledDate = table.Column<DateOnly>(type: "date", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BassRefunds", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PaymentCorrections",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OriginalTreasuryPaymentReceiptId = table.Column<long>(type: "bigint", nullable: false),
                    RedirectedToContributorId = table.Column<long>(type: "bigint", nullable: true),
                    RedirectedToMonth = table.Column<DateOnly>(type: "date", nullable: true),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    AdjustedAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    RequestedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    ApprovedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AppliedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
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
                    table.PrimaryKey("PK_PaymentCorrections", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BassRefunds_CreatedAtUtc",
                schema: "cnas",
                table: "BassRefunds",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_BassRefunds_IsActive",
                schema: "cnas",
                table: "BassRefunds",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_BassRefunds_Status",
                schema: "cnas",
                table: "BassRefunds",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "UX_BassRefunds_Contributor_Month_Active",
                schema: "cnas",
                table: "BassRefunds",
                columns: new[] { "ContributorId", "RelatedMonth" },
                unique: true,
                filter: "\"Status\" <> 4");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentCorrections_CreatedAtUtc",
                schema: "cnas",
                table: "PaymentCorrections",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentCorrections_IsActive",
                schema: "cnas",
                table: "PaymentCorrections",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentCorrections_OriginalReceipt",
                schema: "cnas",
                table: "PaymentCorrections",
                column: "OriginalTreasuryPaymentReceiptId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentCorrections_Status",
                schema: "cnas",
                table: "PaymentCorrections",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BassRefunds",
                schema: "cnas");

            migrationBuilder.DropTable(
                name: "PaymentCorrections",
                schema: "cnas");
        }
    }
}
