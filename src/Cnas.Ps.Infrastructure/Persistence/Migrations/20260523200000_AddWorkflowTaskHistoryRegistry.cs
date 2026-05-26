using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// R0125 / CF 16.09 — placeholder migration for the
    /// <c>cnas.WorkflowTaskStepHistories</c> append-only history projection. The concrete
    /// schema (FK to <c>WorkflowTasks.Id</c>, indexes on (WorkflowTaskId, OccurredAt)
    /// and (EventKind, OccurredAt)) is materialised when the model snapshot is
    /// regenerated at the next migration build. Matches the empty-placeholder pattern
    /// established by neighbouring registry migrations in this batch.
    /// </summary>
    public partial class AddWorkflowTaskHistoryRegistry : Migration
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
