using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// R0057 / TOR SEC 026 + CF 16.11 — placeholder migration for the additive
    /// <c>cnas.DelegationGrants</c> table introduced in iter-141. Backs the
    /// time-bounded delegation lifecycle service
    /// (<c>IDelegationLifecycleService</c>).
    /// </summary>
    /// <remarks>
    /// Deferred-by-design — mirrors the pattern on
    /// <see cref="AddMLogCategoryConfigs"/> /
    /// <see cref="AddDecisionSupersessions"/>. The table is created at first
    /// runtime by EF Core when the in-memory snapshot lands a model change; the
    /// production migration body is filled in alongside the first concrete
    /// Postgres roll-out.
    /// </remarks>
    public partial class AddDelegationGrants : Migration
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
