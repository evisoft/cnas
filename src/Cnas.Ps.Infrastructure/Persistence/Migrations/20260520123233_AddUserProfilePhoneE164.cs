using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Closes the UC13 self-service profile Phone contract drift — adds the
    /// <c>UserProfiles.PhoneE164</c> column to back the historically-stubbed
    /// <c>ProfileOutput.Phone</c> DTO field. Phone numbers are PII per
    /// TOR SEC 035 / CLAUDE.md §5.7 and are stored encrypted at rest via
    /// <see cref="Cnas.Ps.Infrastructure.Persistence.Conversion.EncryptedStringConverter"/>;
    /// the converter wiring lives in <c>CnasDbContext.OnModelCreating</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Schema changes (Up):</b> adds a nullable <c>VARCHAR(128)</c> column
    /// <c>PhoneE164</c> to <c>cnas.UserProfiles</c>. The 128-char width is the
    /// repository's standard envelope for encrypted-string columns — the
    /// canonical E.164 plaintext (≤16 chars) widens to ~59 chars under the
    /// current <c>v1:</c> envelope from
    /// <see cref="Cnas.Ps.Infrastructure.Security.AesFieldEncryptor"/>, with
    /// comfortable headroom for any future <c>v2:</c> envelope so no second
    /// column-widening migration is needed.
    /// </para>
    /// <para>
    /// <b>No index.</b> Equality lookups against an encrypted column are
    /// useless (every encryption samples a fresh nonce, so the same plaintext
    /// encrypts to a different ciphertext per row). Phone is a display field
    /// surfaced through the profile DTO, never a search key, so unlike the
    /// national-identifier pattern (NationalId / Idnp / Idno) we do NOT add a
    /// hash shadow column or any index here.
    /// </para>
    /// <para>
    /// <b>Down:</b> drops the new column. Safe — the column is nullable and
    /// carries no foreign-key dependencies. No data backfill is emitted on the
    /// Down because the column is additive.
    /// </para>
    /// <para>
    /// <b>Hand edits.</b> None. The EF-generated migration shape was kept verbatim;
    /// only this XML doc was added by hand.
    /// </para>
    /// </remarks>
    public partial class AddUserProfilePhoneE164 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PhoneE164",
                schema: "cnas",
                table: "UserProfiles",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PhoneE164",
                schema: "cnas",
                table: "UserProfiles");
        }
    }
}
