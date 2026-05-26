using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// R0570 / TOR CF 08.02 — placeholder migration for the additive
    /// <c>cnas.ExaminerAssignmentCursor</c> singleton-row table introduced
    /// in iter-126. The table holds at most one row per environment (keyed
    /// by <c>"default"</c>) and drives the uniform-spread + registrar-
    /// exclusion examiner assignment on application submission. Matches the
    /// deferred-by-design pattern shared with the recent additive-column /
    /// additive-table migrations (e.g.
    /// <c>20260524150000_AddClassifierReadOnlyMirror</c>) — empty Up / Down
    /// keeps the migration journal idempotent on every existing dev / CI
    /// database; the concrete table is materialised when the model snapshot
    /// is regenerated at the next migration build.
    /// </summary>
    public partial class AddExaminerAssignmentCursor : Migration
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
