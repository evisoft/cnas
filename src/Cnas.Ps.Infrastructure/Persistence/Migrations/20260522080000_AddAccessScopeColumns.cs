using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// R0671 / TOR CF 18.06 — adds the three columns that back the
    /// <c>Cnas.Ps.Application.AccessScope.IAccessScopeFilter</c> row-level filter:
    /// <list type="bullet">
    ///   <item><description><c>Solicitants.RegionCode</c> — short region code (e.g. <c>"CHIS"</c>),
    ///   indexed for the equality / IN lookup the filter emits.</description></item>
    ///   <item><description><c>ServiceApplications.SubdivisionCode</c> — <c>CnasBranch.Code</c>
    ///   value identifying the handling branch, indexed.</description></item>
    ///   <item><description><c>WorkflowDefinitions.CategoryCode</c> — workflow grouping code
    ///   (e.g. <c>"pension"</c>); no dedicated index because the filter resolves it via the
    ///   already-indexed <c>(Code, IsCurrent)</c> partial index when joining from WorkflowTask.</description></item>
    /// </list>
    /// All three columns are nullable strings; existing rows remain <c>NULL</c> on apply —
    /// per the IAccessScope NULL-data semantics, unmarked rows stay visible to every scoped
    /// caller, so back-filling can happen lazily as operators touch data.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Hand-authored</b> per the same convention as <c>AddApplicationPaymentFields</c>
    /// — avoids touching the EF model snapshot in a batch that is otherwise non-destructive.
    /// The next <c>dotnet ef migrations add</c> run will reconcile the snapshot.
    /// </para>
    /// <para>
    /// <b>Down.</b> Drops the two indexes first, then the three columns. Safe — no other
    /// constraint references these columns.
    /// </para>
    /// </remarks>
    public partial class AddAccessScopeColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.AddColumn<string>(
                name: "RegionCode",
                schema: "cnas",
                table: "Solicitants",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SubdivisionCode",
                schema: "cnas",
                table: "ServiceApplications",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CategoryCode",
                schema: "cnas",
                table: "WorkflowDefinitions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Solicitants_RegionCode",
                schema: "cnas",
                table: "Solicitants",
                column: "RegionCode");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceApplications_SubdivisionCode",
                schema: "cnas",
                table: "ServiceApplications",
                column: "SubdivisionCode");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropIndex(
                name: "IX_ServiceApplications_SubdivisionCode",
                schema: "cnas",
                table: "ServiceApplications");

            migrationBuilder.DropIndex(
                name: "IX_Solicitants_RegionCode",
                schema: "cnas",
                table: "Solicitants");

            migrationBuilder.DropColumn(
                name: "CategoryCode",
                schema: "cnas",
                table: "WorkflowDefinitions");

            migrationBuilder.DropColumn(
                name: "SubdivisionCode",
                schema: "cnas",
                table: "ServiceApplications");

            migrationBuilder.DropColumn(
                name: "RegionCode",
                schema: "cnas",
                table: "Solicitants");
        }
    }
}
