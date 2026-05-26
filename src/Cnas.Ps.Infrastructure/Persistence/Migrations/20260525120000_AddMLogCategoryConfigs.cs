using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// R0116 + R0195 / TOR SEC 054-055 — placeholder migration for the additive
    /// <c>cnas.MLogCategoryConfigs</c> table introduced in iter-140. Seeds
    /// default categories from <c>AuditCategory</c> rows already present
    /// (iter 108).
    /// </summary>
    /// <remarks>
    /// Deferred-by-design — see the matching pattern on
    /// <see cref="AddDecisionSupersessions"/>. Seed rows are inserted at first
    /// runtime by the audit drainer's fallback policy (Critical-only mirror)
    /// rather than at migration time so the seed stays in sync with the
    /// operator-tuned filter without conflict-on-conflict gymnastics.
    /// </remarks>
    public partial class AddMLogCategoryConfigs : Migration
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
