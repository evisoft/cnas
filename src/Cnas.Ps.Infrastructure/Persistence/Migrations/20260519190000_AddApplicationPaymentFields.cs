using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Adds the fields required by the MPay payment-dispatch background job:
    /// <list type="bullet">
    ///   <item><description><c>ServiceApplications.PaymentDispatchedAtUtc</c> — idempotency stamp.</description></item>
    ///   <item><description><c>ServiceApplications.PaymentTransactionId</c> — MPay upstream reference.</description></item>
    ///   <item><description><c>ServiceApplications.PaymentStatus</c> — upstream status echo.</description></item>
    ///   <item><description><c>Solicitants.BankIban</c> — beneficiary IBAN for outbound transfers.</description></item>
    ///   <item><description><c>Dossiers.ComputedAmountMdl</c> — decision-engine amount carried into payment.</description></item>
    /// </list>
    /// Hand-authored to avoid touching the EF model snapshot while a concurrent migration
    /// (Document verdict fields) is in flight; the next <c>dotnet ef</c> run will reconcile.
    /// </summary>
    public partial class AddApplicationPaymentFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.AddColumn<DateTime>(
                name: "PaymentDispatchedAtUtc",
                schema: "cnas",
                table: "ServiceApplications",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PaymentTransactionId",
                schema: "cnas",
                table: "ServiceApplications",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PaymentStatus",
                schema: "cnas",
                table: "ServiceApplications",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BankIban",
                schema: "cnas",
                table: "Solicitants",
                type: "character varying(34)",
                maxLength: 34,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ComputedAmountMdl",
                schema: "cnas",
                table: "Dossiers",
                type: "numeric(18,2)",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ServiceApplications_PaymentDispatchedAtUtc",
                schema: "cnas",
                table: "ServiceApplications",
                column: "PaymentDispatchedAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropIndex(
                name: "IX_ServiceApplications_PaymentDispatchedAtUtc",
                schema: "cnas",
                table: "ServiceApplications");

            migrationBuilder.DropColumn(
                name: "ComputedAmountMdl",
                schema: "cnas",
                table: "Dossiers");

            migrationBuilder.DropColumn(
                name: "BankIban",
                schema: "cnas",
                table: "Solicitants");

            migrationBuilder.DropColumn(
                name: "PaymentStatus",
                schema: "cnas",
                table: "ServiceApplications");

            migrationBuilder.DropColumn(
                name: "PaymentTransactionId",
                schema: "cnas",
                table: "ServiceApplications");

            migrationBuilder.DropColumn(
                name: "PaymentDispatchedAtUtc",
                schema: "cnas",
                table: "ServiceApplications");
        }
    }
}
