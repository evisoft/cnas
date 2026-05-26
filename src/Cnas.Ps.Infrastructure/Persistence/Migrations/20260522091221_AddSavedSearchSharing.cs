using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// R0524 / TOR CF 03.06 — extends <c>cnas.SavedSearches</c> with the granular
    /// sharing-scope columns. R0165 already shipped the binary <c>IsShared</c> flag;
    /// R0524 layers on a <c>SharingScope</c> enum (Private / Shared / Group) plus a
    /// nullable <c>SharedWithGroupCode</c> companion column so owners can scope
    /// visibility to a specific user group rather than the entire CNAS staff.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Default-Private back-fill.</b> The <c>SharingScope</c> column is added with
    /// a <c>defaultValue: 0</c> (Private), matching the entity field initialiser.
    /// Existing rows therefore land as Private regardless of their legacy
    /// <c>IsShared</c> value — a deliberate fail-closed back-fill. The application
    /// layer keeps the two columns in sync going forward (create / update / share
    /// paths all maintain both), so the inconsistency only exists for rows created
    /// before the migration is applied; the next mutating call rewrites them.
    /// </para>
    /// <para>
    /// <b>Index rationale.</b> <c>(SharingScope, Registry)</c> backs the discovery
    /// query in <c>SavedSearchService.ListAccessibleAsync</c> (rows where
    /// <c>SharingScope IN (Shared, Group) AND Registry = X</c>). Scope is the
    /// leading axis because the filter narrows to a small literal in-list (2 of 3
    /// values); the planner can short-circuit the registry comparison cheaply for
    /// non-matching scope buckets. The companion <c>SharedWithGroupCode</c> column
    /// is NOT indexed — group membership is matched application-side against the
    /// caller's <c>UserProfile.Groups</c> set, so the column never appears alone in
    /// a WHERE clause.
    /// </para>
    /// <para>
    /// <b>Down migration.</b> Drops the new index and the two columns. Legacy
    /// <c>IsShared</c> stays in place because callers still rely on it.
    /// </para>
    /// </remarks>
    public partial class AddSavedSearchSharing : Migration
    {
        /// <summary>
        /// Adds <c>SharingScope</c> (NOT NULL int, default 0/Private) and
        /// <c>SharedWithGroupCode</c> (nullable varchar(64)) columns, plus the
        /// <c>(SharingScope, Registry)</c> compound index.
        /// </summary>
        /// <param name="migrationBuilder">EF Core migration builder.</param>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.AddColumn<string>(
                name: "SharedWithGroupCode",
                schema: "cnas",
                table: "SavedSearches",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SharingScope",
                schema: "cnas",
                table: "SavedSearches",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_SavedSearches_SharingScope_Registry",
                schema: "cnas",
                table: "SavedSearches",
                columns: new[] { "SharingScope", "Registry" });
        }

        /// <summary>
        /// Drops the <c>(SharingScope, Registry)</c> index, then the
        /// <c>SharingScope</c> and <c>SharedWithGroupCode</c> columns.
        /// </summary>
        /// <param name="migrationBuilder">EF Core migration builder.</param>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropIndex(
                name: "IX_SavedSearches_SharingScope_Registry",
                schema: "cnas",
                table: "SavedSearches");

            migrationBuilder.DropColumn(
                name: "SharedWithGroupCode",
                schema: "cnas",
                table: "SavedSearches");

            migrationBuilder.DropColumn(
                name: "SharingScope",
                schema: "cnas",
                table: "SavedSearches");
        }
    }
}
