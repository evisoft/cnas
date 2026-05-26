using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// R0502 / TOR CF 01.05 — adds the <c>Category</c> column + index to
    /// <c>cnas.ServicePassports</c>. The public services-catalog endpoint exposes
    /// <see cref="Cnas.Ps.Core.Domain.ServicePassport.Category"/> as an optional filter
    /// dimension; the index supports equality matches without sequential scans once the
    /// catalogue grows past trivial size.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Schema changes (Up):</b> adds a nullable <c>VARCHAR(64)</c> column
    /// <c>Category</c> to <c>cnas.ServicePassports</c> plus a non-unique index on the
    /// column. Existing rows surface as <c>NULL</c>; administrators back-fill the
    /// category code via the standard passport edit pathway. The column is intentionally
    /// nullable so legacy seed rows continue to load while taxonomy work proceeds
    /// in parallel.
    /// </para>
    /// <para>
    /// <b>Down:</b> drops the index and the column. Safe — the column is nullable and
    /// carries no foreign-key dependencies.
    /// </para>
    /// </remarks>
    public partial class AddServicePassportCategory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.AddColumn<string>(
                name: "Category",
                schema: "cnas",
                table: "ServicePassports",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ServicePassports_Category",
                schema: "cnas",
                table: "ServicePassports",
                column: "Category");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropIndex(
                name: "IX_ServicePassports_Category",
                schema: "cnas",
                table: "ServicePassports");

            migrationBuilder.DropColumn(
                name: "Category",
                schema: "cnas",
                table: "ServicePassports");
        }
    }
}
