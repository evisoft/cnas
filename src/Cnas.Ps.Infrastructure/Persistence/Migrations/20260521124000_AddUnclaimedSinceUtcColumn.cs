using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// R0202 / CF 20.05 — adds the <c>WorkflowTasks.UnclaimedSinceUtc</c> timestamp
    /// column that drives the unclaimed-task escalation SLA (
    /// <c>UnclaimedTaskEscalationJob</c>), plus the non-clustered index that backs
    /// the job's filter
    /// (<c>Status == Pending AND AssignedUserId IS NULL AND UnclaimedSinceUtc &lt;= deadline</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Back-fill recipe.</b> The column is added nullable so existing rows materialise
    /// with <c>NULL</c>. For rows currently in the "unclaimed pool" — i.e.
    /// <see cref="Cnas.Ps.Core.Domain.WorkflowTaskStatus.Pending"/> (status value
    /// <c>0</c>), <c>AssignedUserId IS NULL</c>, <c>GroupCode IS NOT NULL</c>, and
    /// <c>IsActive = TRUE</c> — we copy <c>CreatedAtUtc</c> into the new column as a
    /// best-effort approximation of "when did the row enter the unclaimed pool?".
    /// Tasks that have been waiting longer than the configured window will be picked
    /// up by the next escalation sweep, which is the correct behaviour: the team has
    /// already missed the SLA.
    /// </para>
    /// <para>
    /// <b>InMemory test provider.</b> The raw-SQL back-fill is a no-op on the in-memory
    /// provider; tests seed the column directly via the entity setter and never see
    /// the SQL path. PostgreSQL applies it transactionally alongside the column add.
    /// </para>
    /// </remarks>
    public partial class AddUnclaimedSinceUtcColumn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.AddColumn<DateTime>(
                name: "UnclaimedSinceUtc",
                schema: "cnas",
                table: "WorkflowTasks",
                type: "timestamp with time zone",
                nullable: true);

            // R0202 — Best-effort back-fill of pre-migration tasks currently in the
            // unclaimed pool (Status = 0 Pending, AssignedUserId IS NULL, GroupCode set).
            // CreatedAtUtc is the closest proxy for "when did the row enter the
            // unclaimed pool?" available without a status-history table. Tasks already
            // past the configured window will be picked up by the next sweep — correct
            // behaviour for the SLA.
            migrationBuilder.Sql(@"
UPDATE cnas.""WorkflowTasks""
SET ""UnclaimedSinceUtc"" = ""CreatedAtUtc""
WHERE ""Status"" = 0
  AND ""AssignedUserId"" IS NULL
  AND ""GroupCode"" IS NOT NULL
  AND ""IsActive"" = TRUE;
");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowTasks_UnclaimedSinceUtc",
                schema: "cnas",
                table: "WorkflowTasks",
                column: "UnclaimedSinceUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropIndex(
                name: "IX_WorkflowTasks_UnclaimedSinceUtc",
                schema: "cnas",
                table: "WorkflowTasks");

            migrationBuilder.DropColumn(
                name: "UnclaimedSinceUtc",
                schema: "cnas",
                table: "WorkflowTasks");
        }
    }
}
