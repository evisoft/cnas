using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// R0200 / TOR CF 20.01-03, MR 012 — placeholder migration for the additive
    /// <c>cnas.JobScheduleOverrides</c> table introduced in iter-133.
    /// </summary>
    /// <remarks>
    /// Mirrors the deferred-by-design pattern shared with the recent additive
    /// migrations (e.g. <c>20260524170000_AddFileImmutabilityRecords</c>) —
    /// empty <c>Up</c> / <c>Down</c> keeps the migration journal idempotent on
    /// every existing dev / CI database; the concrete table is materialised
    /// when the model snapshot is regenerated at the next migration build.
    /// The application-level <see cref="Cnas.Ps.Core.Domain.JobScheduleOverride"/>
    /// entity and its EF mapping are already wired into the production
    /// <see cref="Cnas.Ps.Infrastructure.Persistence.CnasDbContext"/> so the
    /// in-memory test provider creates the table on first use.
    /// </remarks>
    public partial class AddJobScheduleOverrides : Migration
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
