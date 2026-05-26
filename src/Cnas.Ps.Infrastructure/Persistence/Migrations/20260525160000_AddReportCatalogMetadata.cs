using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// R1900-R1905 / iter-145 — placeholder migration for the additive
    /// <c>cnas.Reports</c> catalog-metadata columns (<c>NameRo</c>,
    /// <c>Purpose</c>, <c>Audience</c>, <c>Frequency</c>, <c>ColumnsJson</c>,
    /// <c>RbacRole</c>, <c>Schedule</c>, <c>OutputFormatsJson</c>,
    /// <c>Category</c>) introduced by the report-catalog seeder.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Mirrors the deferred-by-design pattern shared with the recent additive
    /// migrations (see
    /// <see cref="AddEntityAttributeValues"/> /
    /// <see cref="AddOfflineBatchJobs"/>) — empty <c>Up</c> / <c>Down</c>
    /// keeps the migration journal idempotent on every existing dev / CI
    /// database. The concrete columns are materialised when the model
    /// snapshot is regenerated at the next migration build. The
    /// application-level <see cref="Cnas.Ps.Core.Domain.Report"/> entity and
    /// its EF mapping in
    /// <see cref="Cnas.Ps.Infrastructure.Persistence.Configurations.ReportConfiguration"/>
    /// are already wired into the production
    /// <see cref="Cnas.Ps.Infrastructure.Persistence.CnasDbContext"/> so the
    /// in-memory test provider creates the columns on first use.
    /// </para>
    /// </remarks>
    public partial class AddReportCatalogMetadata : Migration
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
