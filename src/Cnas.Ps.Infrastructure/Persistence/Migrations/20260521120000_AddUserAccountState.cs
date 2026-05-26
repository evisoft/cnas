using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// R0059 / SEC 016 — Replaces the boolean <c>IsLocked</c> column on
    /// <c>cnas.UserProfiles</c> with the integer-backed <c>State</c> column that maps to
    /// <see cref="Cnas.Ps.Core.Domain.UserAccountState"/>
    /// (<c>Active=0 / Suspended=1 / Disabled=2 / Locked=3</c>). Back-fills
    /// <c>IsLocked = TRUE</c> rows to <c>State = 3 (Locked)</c> before dropping the
    /// legacy column.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Up-migration sequence (PostgreSQL-safe, single transaction):
    /// <list type="number">
    ///   <item>Add the <c>State</c> column with server default <c>0</c> (Active) so every
    ///         existing row materialises as Active.</item>
    ///   <item>Back-fill: any row that was previously <c>IsLocked = TRUE</c> moves to
    ///         <c>State = 3</c> (Locked) so the lock survives the migration verbatim.</item>
    ///   <item>Create the non-clustered <c>IX_UserProfiles_State</c> index supporting
    ///         the per-state admin filters ("list all Suspended users", future bulk
    ///         reactivation pipelines).</item>
    ///   <item>Drop the <c>IsLocked</c> column (no prior index existed on it).</item>
    /// </list>
    /// </para>
    /// <para>
    /// Down-migration reverses the column shape: re-adds <c>IsLocked</c> as a non-nullable
    /// boolean with server default <c>false</c>, back-fills it from <c>State = 3</c>
    /// (Locked → true; every other state → false), drops the <c>State</c> index, and
    /// drops the <c>State</c> column. Non-Locked states cannot be round-tripped through
    /// a single boolean — the Down path is lossy by design and is intended only for
    /// emergency rollback within the same release window.
    /// </para>
    /// <para>
    /// The EF InMemory provider used by the unit tests skips raw SQL harmlessly; Postgres
    /// applies the back-fill transactionally alongside the column operations.
    /// </para>
    /// </remarks>
    public partial class AddUserAccountState : Migration
    {
        /// <summary>Adds the <c>State</c> column + index, back-fills locked rows, drops the legacy <c>IsLocked</c> column.</summary>
        /// <param name="migrationBuilder">EF Core migration builder.</param>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Add the State column with the Active default (0). Existing rows pick up
            //    the default in the same transaction so the column is non-nullable from
            //    the start.
            migrationBuilder.AddColumn<int>(
                name: "State",
                schema: "cnas",
                table: "UserProfiles",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // 2. Back-fill the locked rows BEFORE dropping the legacy column so the
            //    intent (a locked account stays locked) survives the migration.
            //    InMemory providers ignore raw SQL; PostgreSQL applies it transactionally.
            migrationBuilder.Sql(
                "UPDATE cnas.\"UserProfiles\" SET \"State\" = 3 WHERE \"IsLocked\" = TRUE;");

            // 3. Index supporting the "list users in state X" admin queries.
            migrationBuilder.CreateIndex(
                name: "IX_UserProfiles_State",
                schema: "cnas",
                table: "UserProfiles",
                column: "State");

            // 4. Drop the now-redundant IsLocked column (no prior index existed on it).
            migrationBuilder.DropColumn(
                name: "IsLocked",
                schema: "cnas",
                table: "UserProfiles");
        }

        /// <summary>
        /// Reverses the column shape. Lossy for Suspended / Disabled rows — both collapse
        /// to <c>IsLocked = FALSE</c> on the way back. See class remarks.
        /// </summary>
        /// <param name="migrationBuilder">EF Core migration builder.</param>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 1. Re-add the IsLocked column with the false default.
            migrationBuilder.AddColumn<bool>(
                name: "IsLocked",
                schema: "cnas",
                table: "UserProfiles",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // 2. Back-fill: Locked (3) flips to TRUE; every other state collapses to
            //    FALSE. Suspended / Disabled rows lose their richer state classification
            //    — the Down migration is intended for emergency rollback within the
            //    same release window where this loss is acceptable.
            migrationBuilder.Sql(
                "UPDATE cnas.\"UserProfiles\" SET \"IsLocked\" = TRUE WHERE \"State\" = 3;");

            // 3. Drop the State index + column in that order (index first).
            migrationBuilder.DropIndex(
                name: "IX_UserProfiles_State",
                schema: "cnas",
                table: "UserProfiles");

            migrationBuilder.DropColumn(
                name: "State",
                schema: "cnas",
                table: "UserProfiles");
        }
    }
}
