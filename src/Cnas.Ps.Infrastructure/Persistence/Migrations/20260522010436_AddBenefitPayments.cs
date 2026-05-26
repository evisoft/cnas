using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// R0517 / TOR CF 02.05 — adds the citizen benefit-payment ledger
    /// (<c>cnas.BenefitPayments</c>) that backs the authenticated
    /// "status of pension / allowance payments" endpoint. No seed data —
    /// payment rows are created upstream by the (deferred) MTreasury / IPS
    /// reconciliation adapter; for now operators load rows manually for
    /// pilot testing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Schema changes (Up).</b> Creates the BenefitPayments table with
    /// the standard auditable-entity convention (xmin concurrency token,
    /// soft-delete via <c>IsActive</c>). The natural-key uniqueness
    /// constraint on <c>(BeneficiarySolicitantId, BenefitType, PaymentMonth)</c>
    /// matches the entity remarks ("at most one payment per beneficiary per
    /// benefit per month"). A secondary index on
    /// <c>(BeneficiarySolicitantId, PaymentMonth)</c> supports the
    /// status-lookup query path. The standard <c>IsActive</c> and
    /// <c>CreatedAtUtc</c> indexes come from
    /// <c>AuditableEntityConfiguration</c>.
    /// </para>
    /// <para>
    /// <b>No foreign-key constraint.</b> The application layer enforces the
    /// FK from BenefitPayment to Solicitant via the indexed
    /// <c>BeneficiarySolicitantId</c> column; explicit DB-level cascades are
    /// deliberately omitted so the soft-delete sweep can mark Solicitant
    /// rows inactive without orphaning payment ledger entries (the audit
    /// trail must outlive the principal record).
    /// </para>
    /// <para>
    /// <b>Down.</b> Drops the table (and its indexes by cascade). Safe —
    /// no other table in this revision references BenefitPayments.
    /// </para>
    /// </remarks>
    public partial class AddBenefitPayments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.CreateTable(
                name: "BenefitPayments",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BeneficiarySolicitantId = table.Column<long>(type: "bigint", nullable: false),
                    BenefitType = table.Column<int>(type: "integer", nullable: false),
                    PaymentMonth = table.Column<DateOnly>(type: "date", nullable: false),
                    GrossAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    NetAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    TaxWithheld = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Method = table.Column<int>(type: "integer", nullable: false),
                    BankAccountIban = table.Column<string>(type: "character varying(34)", maxLength: 34, nullable: true),
                    PostalOrderNumber = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IssuedDate = table.Column<DateOnly>(type: "date", nullable: true),
                    PaidDate = table.Column<DateOnly>(type: "date", nullable: true),
                    ReturnedDate = table.Column<DateOnly>(type: "date", nullable: true),
                    ReturnReason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BenefitPayments", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BenefitPayments_Beneficiary_Month",
                schema: "cnas",
                table: "BenefitPayments",
                columns: new[] { "BeneficiarySolicitantId", "PaymentMonth" });

            migrationBuilder.CreateIndex(
                name: "IX_BenefitPayments_CreatedAtUtc",
                schema: "cnas",
                table: "BenefitPayments",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_BenefitPayments_IsActive",
                schema: "cnas",
                table: "BenefitPayments",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "UX_BenefitPayments_NaturalKey",
                schema: "cnas",
                table: "BenefitPayments",
                columns: new[] { "BeneficiarySolicitantId", "BenefitType", "PaymentMonth" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropTable(
                name: "BenefitPayments",
                schema: "cnas");
        }
    }
}
