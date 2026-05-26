using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// R2190-R2200 / TOR §15.6 FLEX 006 — placeholder migration for the additive
    /// <c>cnas.EntityAttributeValues</c> EAV sidecar introduced in iter-144.
    /// Backs <c>IDynamicAttributeService</c>.
    /// </summary>
    /// <remarks>
    /// Deferred-by-design — mirrors the pattern on
    /// <see cref="AddDelegationGrants"/> / <see cref="AddOfflineBatchJobs"/>.
    /// The table is created at first runtime by EF Core when the snapshot lands
    /// the model change; the production migration body is filled in alongside
    /// the first concrete Postgres roll-out.
    /// </remarks>
    public partial class AddEntityAttributeValues : Migration
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
