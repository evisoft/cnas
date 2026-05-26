using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// R2161 / TOR INT 002 — placeholder migration for the additive
    /// <c>cnas.OfflineBatchJobs</c> table introduced in iter-142. Backs the
    /// generic CnasUser-facing offline-batch ingest / export service
    /// (<c>IOfflineBatchService</c>).
    /// </summary>
    /// <remarks>
    /// Deferred-by-design — mirrors the pattern on
    /// <see cref="AddDelegationGrants"/> / <see cref="AddMLogCategoryConfigs"/>.
    /// The table is created at first runtime by EF Core when the in-memory
    /// snapshot lands a model change; the production migration body is filled
    /// in alongside the first concrete Postgres roll-out.
    /// </remarks>
    public partial class AddOfflineBatchJobs : Migration
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
