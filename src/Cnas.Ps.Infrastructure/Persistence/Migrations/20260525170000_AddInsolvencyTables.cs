using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// R0830 / R0834 / TOR Annex 1 §8.1.4.5 — placeholder migration for the
    /// additive insolvency lifecycle tables (<c>cnas.InsolvencyCases</c>,
    /// <c>cnas.InsolvencyClaims</c>, <c>cnas.InsolvencyPayments</c>) introduced
    /// in iter-146. Backs the dedicated <c>IInsolvencyLifecycleService</c> that
    /// splits the historical <c>Contributor.IsInsolvent</c> flag (one bit, no
    /// history) into a fully-audited per-event registry with its own claims +
    /// payments sub-tables.
    /// </summary>
    /// <remarks>
    /// Deferred-by-design — mirrors the pattern on
    /// <see cref="AddDelegationGrants"/> / <see cref="AddOfflineBatchJobs"/> /
    /// <see cref="AddPaymentSuspensionRecords"/>. The tables are created at
    /// first runtime by EF Core when the in-memory snapshot lands the model
    /// change; the production migration body is filled in alongside the first
    /// concrete Postgres roll-out.
    /// </remarks>
    public partial class AddInsolvencyTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
