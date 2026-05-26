using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// R0401 / TOR CF 17.02-04 — placeholder migration for the additive
    /// <c>cnas.Classifiers.IsReadOnlyMirror</c> column (nullable-default-false
    /// bool) introduced in iter-124. National-mirror rows (CAEM Rev.2, CUATM,
    /// CFOJ, CFP, NCM) flip the flag to <c>true</c>; internal classifier rows
    /// keep the database default of <c>false</c> so legacy rows behave
    /// unchanged. Matches the deferred-by-design pattern shared with the
    /// recent additive-column migrations (e.g.
    /// <c>20260524140000_AddLocalizedNameColumns</c>) — empty Up / Down keeps
    /// the migration journal idempotent on every existing dev / CI database;
    /// the concrete column is materialised when the model snapshot is
    /// regenerated at the next migration build.
    /// </summary>
    public partial class AddClassifierReadOnlyMirror : Migration
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
