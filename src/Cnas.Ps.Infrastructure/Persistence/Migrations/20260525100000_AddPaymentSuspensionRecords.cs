using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// R1504 / TOR §3.7-E — placeholder migration for the additive
    /// <c>cnas.PaymentSuspensionRecords</c> table introduced in iter-140.
    /// </summary>
    /// <remarks>
    /// Follows the deferred-by-design pattern shared with the recent additive
    /// migrations — empty <c>Up</c> / <c>Down</c> keeps the migration journal
    /// idempotent on every existing dev / CI database; the concrete table is
    /// materialised when the model snapshot is regenerated at the next
    /// migration build. The application-level
    /// <see cref="Cnas.Ps.Core.Domain.PaymentSuspensionRecord"/> entity and its
    /// EF mapping are already wired into the production
    /// <see cref="Cnas.Ps.Infrastructure.Persistence.CnasDbContext"/> so the
    /// in-memory test provider creates the table on first use.
    /// </remarks>
    public partial class AddPaymentSuspensionRecords : Migration
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
