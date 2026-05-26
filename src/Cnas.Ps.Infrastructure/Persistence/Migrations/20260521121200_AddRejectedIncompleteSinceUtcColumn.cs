using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// R0934 / TOR §2.5.1 — adds the <c>ServiceApplications.RejectedIncompleteSinceUtc</c>
    /// timestamp column that drives the 30-day missing-documents auto-close SLA, plus
    /// the non-clustered index that backs the <c>MissingDocsSlaJob</c> filter
    /// (<c>Status == RejectedIncomplete AND RejectedIncompleteSinceUtc &lt;= deadline</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Back-fill recipe.</b> The column is added nullable so existing rows materialise
    /// with <c>NULL</c>. For rows currently in <see cref="Cnas.Ps.Core.Domain.ApplicationStatus.RejectedIncomplete"/>
    /// (status value <c>2</c>) we copy <c>UpdatedAtUtc</c> into the new column as a
    /// best-effort approximation of "when did the row most recently enter that state?".
    /// </para>
    /// <para>
    /// <b>SLA semantics on back-fill.</b> Pre-migration rows that have already been in
    /// <c>RejectedIncomplete</c> longer than 30 days will auto-close on the next
    /// <c>MissingDocsSlaJob</c> fire — that is the correct behaviour: the citizen has
    /// already missed the window. Rows whose <c>UpdatedAtUtc</c> is recent get a fresh
    /// 30-day clock starting from the migration moment, which is more lenient than the
    /// strict policy but cannot be reconstructed without inspecting the (yet-to-be-built)
    /// status-history audit trail.
    /// </para>
    /// <para>
    /// <b>InMemory test provider.</b> The raw-SQL back-fill is a no-op on the in-memory
    /// provider; tests seed the column directly via the entity setter and never see the
    /// SQL path. PostgreSQL applies it transactionally alongside the column add.
    /// </para>
    /// </remarks>
    public partial class AddRejectedIncompleteSinceUtcColumn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.AddColumn<DateTime>(
                name: "RejectedIncompleteSinceUtc",
                schema: "cnas",
                table: "ServiceApplications",
                type: "timestamp with time zone",
                nullable: true);

            // R0934 — Best-effort back-fill of pre-migration rows currently parked in
            // RejectedIncomplete (status value 2). UpdatedAtUtc is the closest proxy for
            // "when did the row enter this state?" available without a status-history
            // table. Rows already >30 days old will be picked up by the next sweep —
            // the correct behaviour for the SLA.
            migrationBuilder.Sql(@"
UPDATE cnas.""ServiceApplications""
SET ""RejectedIncompleteSinceUtc"" = ""UpdatedAtUtc""
WHERE ""Status"" = 2 AND ""RejectedIncompleteSinceUtc"" IS NULL;
");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceApplications_RejectedIncompleteSinceUtc",
                schema: "cnas",
                table: "ServiceApplications",
                column: "RejectedIncompleteSinceUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropIndex(
                name: "IX_ServiceApplications_RejectedIncompleteSinceUtc",
                schema: "cnas",
                table: "ServiceApplications");

            migrationBuilder.DropColumn(
                name: "RejectedIncompleteSinceUtc",
                schema: "cnas",
                table: "ServiceApplications");
        }
    }
}
