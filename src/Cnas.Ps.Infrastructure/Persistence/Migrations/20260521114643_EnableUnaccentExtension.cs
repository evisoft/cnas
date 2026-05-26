using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// R0162 / CF 03.13 — enables the Postgres <c>unaccent</c> extension so the four
    /// registry search sites (<c>ContributorService.SearchAsync</c>,
    /// <c>InsuredPersonService.SearchAsync</c>,
    /// <c>DataSearchService.SearchContributorsAsync</c> /
    /// <c>SearchInsuredAsync</c>, and <c>PublicContentService.SearchAsync</c>)
    /// can fold diacritics off both the column and the LIKE pattern at query time
    /// via <c>unaccent(col) ILIKE unaccent(pattern)</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Extension scope.</b> Installed into the <c>public</c> schema (matching
    /// pgcrypto's installation site in
    /// <c>20260521103646_AddAuditLogHashChain</c>) so the C# binding
    /// <c>HasDbFunction(... ).HasSchema("public").HasName("unaccent")</c> in
    /// <c>CnasDbContext.OnModelCreating</c> resolves it. <c>CREATE EXTENSION IF NOT
    /// EXISTS</c> is idempotent so a repeat apply on a database that already has the
    /// extension is a no-op.
    /// </para>
    /// <para>
    /// <b>InMemory test provider.</b> The raw-SQL is a no-op against the InMemory
    /// provider; tests instead route through the in-process
    /// <see cref="Cnas.Ps.Application.Search.DiacriticFolding"/> fallback in each
    /// search site's <c>else</c> branch. PostgreSQL applies the extension
    /// transactionally.
    /// </para>
    /// <para>
    /// <b>Privileges.</b> <c>CREATE EXTENSION</c> requires <c>CREATE</c> on the target
    /// schema and (for unaccent) <c>postgres</c> superuser privileges on most managed
    /// services. CNAS's deploy pipeline already runs migrations as the database owner
    /// (see <c>deploy/scripts</c>); the extension lands at first deploy of this
    /// migration. If superuser is not available, install the extension out-of-band and
    /// the <c>IF NOT EXISTS</c> clause turns this migration into a no-op.
    /// </para>
    /// <para>
    /// <b>Model snapshot.</b> The companion <c>HasDbFunction</c> registration in
    /// <c>CnasDbContext.OnModelCreating</c> is captured in the Designer snapshot as a
    /// model-level annotation; the SQL side of this migration just installs the
    /// physical extension the C# binding depends on.
    /// </para>
    /// </remarks>
    public partial class EnableUnaccentExtension : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            // R0162 — install unaccent into the public schema; idempotent.
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS unaccent;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            // Rolling back drops the extension — but only when no dependent objects
            // remain. Search columns that reference unaccent() through the EF query
            // shape are not stored objects (no functional indexes, no materialised
            // views), so the drop succeeds on a clean database. If a later migration
            // adds a functional index over unaccent(col), that migration must be
            // rolled back FIRST.
            migrationBuilder.Sql("DROP EXTENSION IF EXISTS unaccent;");
        }
    }
}
