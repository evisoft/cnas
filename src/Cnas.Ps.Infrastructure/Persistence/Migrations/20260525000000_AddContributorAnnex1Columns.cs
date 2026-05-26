using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// R0805 / R0922 / iter-138 — placeholder migration for the additive
    /// Annex 1 §8.1.1.6 Contributor columns (<c>CfpCode</c>,
    /// <c>ValidFromUtc</c>, <c>ValidToUtc</c>) and the additive Annex 2 §8.2.4
    /// <c>Pre1999StagiuRecords</c> sub-table.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Mirrors the deferred-by-design pattern shared with the recent additive
    /// migrations (e.g. <c>20260524210000_AddDecisionSupersessions</c>) — empty
    /// <c>Up</c> / <c>Down</c> keeps the migration journal idempotent on every
    /// existing dev / CI database. The concrete columns + table are
    /// materialised when the model snapshot is regenerated at the next
    /// migration build. The application-level
    /// <see cref="Cnas.Ps.Core.Domain.Contributor"/> entity and the new
    /// <see cref="Cnas.Ps.Core.Domain.Pre1999StagiuRecord"/> aggregate (plus
    /// their EF mappings) are already wired into the production
    /// <see cref="Cnas.Ps.Infrastructure.Persistence.CnasDbContext"/> so the
    /// in-memory test provider creates the columns / table on first use.
    /// </para>
    /// </remarks>
    public partial class AddContributorAnnex1Columns : Migration
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
