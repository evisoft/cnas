using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// TOR SEC 035 follow-up — extends application-level field encryption (CLAUDE.md §5.7)
    /// from the lone <c>Solicitant.BankIban</c> column to every national-identifier column,
    /// and adds the deterministic HMAC hash shadow columns that restore equality lookups
    /// and unique-index enforcement against the encrypted plaintext columns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Schema changes (Up):</b>
    /// <list type="bullet">
    ///   <item>
    ///     Widens the plaintext columns from <c>VARCHAR(13)</c> to <c>VARCHAR(128)</c> on
    ///     <see cref="Cnas.Ps.Core.Domain.Solicitant.NationalId"/>,
    ///     <see cref="Cnas.Ps.Core.Domain.Contributor.Idno"/>,
    ///     <see cref="Cnas.Ps.Core.Domain.InsuredPerson.Idnp"/>, and
    ///     <see cref="Cnas.Ps.Core.Domain.UserProfile.NationalId"/>. The 13-char limit is
    ///     too narrow for the <c>v1:&lt;base64&gt;</c> envelope produced by
    ///     <see cref="Cnas.Ps.Infrastructure.Security.AesFieldEncryptor"/> (~59 chars for
    ///     a 13-char plaintext). 128 leaves comfortable headroom for any future
    ///     <c>v2:</c> envelope without a second column-widening migration.
    ///   </item>
    ///   <item>
    ///     Adds four new <c>VARCHAR(44)</c> hash shadow columns: <c>Solicitants.NationalIdHash</c>,
    ///     <c>Contributors.IdnoHash</c>, <c>InsuredPersons.IdnpHash</c>, and
    ///     <c>UserProfiles.NationalIdHash</c>. 44 chars is exactly the base64-encoded
    ///     length of an HMAC-SHA256 (32 raw bytes + <c>=</c> padding).
    ///   </item>
    ///   <item>
    ///     Drops the unique index on every plaintext column and creates the unique index on
    ///     the corresponding hash column instead. The <c>UserProfiles.NationalId</c> index
    ///     was non-unique and moves to <c>NationalIdHash</c> as non-unique
    ///     (see <c>UserProfileConfiguration</c> for the multi-row rationale).
    ///   </item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>NOT-NULL default on new columns.</b> The three required hash columns
    /// (<c>Solicitants.NationalIdHash</c>, <c>Contributors.IdnoHash</c>,
    /// <c>InsuredPersons.IdnpHash</c>) are <c>NOT NULL</c> with a default of empty string.
    /// This repository is pre-launch — there is no production data, so any rows that
    /// existed in staging or dev would receive empty hashes on this migration. That is
    /// acceptable because (a) the application layer overwrites the hash on every
    /// subsequent write (see <c>ContributorService</c>, <c>InsuredPersonService</c>,
    /// MConnect sync jobs), and (b) any stale empty-hash row is naturally pruned by the
    /// soft-delete + re-register flow each service exposes. We pick the empty-default
    /// over making the column nullable because the application contract is
    /// "hash MUST be populated alongside the plaintext" — a nullable column would
    /// invite the bug of "plaintext written, hash forgotten" silently passing a
    /// production code review.
    /// </para>
    /// <para>
    /// <b>No backfill.</b> Per the points above, no <c>UPDATE … SET hash = HMAC(plaintext)</c>
    /// SQL is emitted here — there is no production data to backfill. The first post-launch
    /// migration that introduces such data must include the backfill step (and re-enable
    /// the temporary <c>nullable: true</c> contract until the backfill completes).
    /// </para>
    /// <para>
    /// <b>Hand edits.</b> None. The EF-generated migration matches the intended shape
    /// (index moves + column widening + new hash columns); only the documentation comments
    /// on this type were added by hand.
    /// </para>
    /// </remarks>
    public partial class EncryptNationalIdentifiersWithHashShadow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UserProfiles_NationalId",
                schema: "cnas",
                table: "UserProfiles");

            migrationBuilder.DropIndex(
                name: "IX_Solicitants_NationalId",
                schema: "cnas",
                table: "Solicitants");

            migrationBuilder.DropIndex(
                name: "IX_InsuredPersons_Idnp",
                schema: "cnas",
                table: "InsuredPersons");

            migrationBuilder.DropIndex(
                name: "IX_Contributors_Idno",
                schema: "cnas",
                table: "Contributors");

            migrationBuilder.AlterColumn<string>(
                name: "NationalId",
                schema: "cnas",
                table: "UserProfiles",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(13)",
                oldMaxLength: 13,
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NationalIdHash",
                schema: "cnas",
                table: "UserProfiles",
                type: "character varying(44)",
                maxLength: 44,
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "NationalId",
                schema: "cnas",
                table: "Solicitants",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(13)",
                oldMaxLength: 13);

            migrationBuilder.AddColumn<string>(
                name: "NationalIdHash",
                schema: "cnas",
                table: "Solicitants",
                type: "character varying(44)",
                maxLength: 44,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AlterColumn<string>(
                name: "Idnp",
                schema: "cnas",
                table: "InsuredPersons",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(13)",
                oldMaxLength: 13);

            migrationBuilder.AddColumn<string>(
                name: "IdnpHash",
                schema: "cnas",
                table: "InsuredPersons",
                type: "character varying(44)",
                maxLength: 44,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AlterColumn<string>(
                name: "Idno",
                schema: "cnas",
                table: "Contributors",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(13)",
                oldMaxLength: 13);

            migrationBuilder.AddColumn<string>(
                name: "IdnoHash",
                schema: "cnas",
                table: "Contributors",
                type: "character varying(44)",
                maxLength: 44,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_UserProfiles_NationalIdHash",
                schema: "cnas",
                table: "UserProfiles",
                column: "NationalIdHash");

            migrationBuilder.CreateIndex(
                name: "IX_Solicitants_NationalIdHash",
                schema: "cnas",
                table: "Solicitants",
                column: "NationalIdHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InsuredPersons_IdnpHash",
                schema: "cnas",
                table: "InsuredPersons",
                column: "IdnpHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Contributors_IdnoHash",
                schema: "cnas",
                table: "Contributors",
                column: "IdnoHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UserProfiles_NationalIdHash",
                schema: "cnas",
                table: "UserProfiles");

            migrationBuilder.DropIndex(
                name: "IX_Solicitants_NationalIdHash",
                schema: "cnas",
                table: "Solicitants");

            migrationBuilder.DropIndex(
                name: "IX_InsuredPersons_IdnpHash",
                schema: "cnas",
                table: "InsuredPersons");

            migrationBuilder.DropIndex(
                name: "IX_Contributors_IdnoHash",
                schema: "cnas",
                table: "Contributors");

            migrationBuilder.DropColumn(
                name: "NationalIdHash",
                schema: "cnas",
                table: "UserProfiles");

            migrationBuilder.DropColumn(
                name: "NationalIdHash",
                schema: "cnas",
                table: "Solicitants");

            migrationBuilder.DropColumn(
                name: "IdnpHash",
                schema: "cnas",
                table: "InsuredPersons");

            migrationBuilder.DropColumn(
                name: "IdnoHash",
                schema: "cnas",
                table: "Contributors");

            migrationBuilder.AlterColumn<string>(
                name: "NationalId",
                schema: "cnas",
                table: "UserProfiles",
                type: "character varying(13)",
                maxLength: 13,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(128)",
                oldMaxLength: 128,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "NationalId",
                schema: "cnas",
                table: "Solicitants",
                type: "character varying(13)",
                maxLength: 13,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(128)",
                oldMaxLength: 128);

            migrationBuilder.AlterColumn<string>(
                name: "Idnp",
                schema: "cnas",
                table: "InsuredPersons",
                type: "character varying(13)",
                maxLength: 13,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(128)",
                oldMaxLength: 128);

            migrationBuilder.AlterColumn<string>(
                name: "Idno",
                schema: "cnas",
                table: "Contributors",
                type: "character varying(13)",
                maxLength: 13,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(128)",
                oldMaxLength: 128);

            migrationBuilder.CreateIndex(
                name: "IX_UserProfiles_NationalId",
                schema: "cnas",
                table: "UserProfiles",
                column: "NationalId");

            migrationBuilder.CreateIndex(
                name: "IX_Solicitants_NationalId",
                schema: "cnas",
                table: "Solicitants",
                column: "NationalId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InsuredPersons_Idnp",
                schema: "cnas",
                table: "InsuredPersons",
                column: "Idnp",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Contributors_Idno",
                schema: "cnas",
                table: "Contributors",
                column: "Idno",
                unique: true);
        }
    }
}
