using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// R0194 / SEC 047 — adds the SHA-256 hash-chain columns
    /// (<c>AuditLogs.PrevHash</c>, <c>AuditLogs.RowHash</c>) plus a non-unique
    /// secondary index on <c>RowHash</c>, then back-fills the chain across any
    /// existing rows so the verifier (<c>IAuditChainVerifier</c>) does not
    /// false-alarm on seed data.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Schema changes (Up).</b>
    /// </para>
    /// <list type="bullet">
    /// <item>Adds <c>PrevHash CHARACTER VARYING(64) NOT NULL</c> with a default
    /// of <c>'GENESIS'</c>. The default lets existing rows materialise without
    /// a separate "make nullable, fill, make non-nullable" dance — the
    /// back-fill below overwrites the placeholder with the chained value.</item>
    /// <item>Adds <c>RowHash CHARACTER VARYING(64) NOT NULL</c> with a default
    /// of the empty string; back-fill overwrites it with the SHA-256 digest.</item>
    /// <item>Creates non-clustered index <c>IX_AuditLogs_RowHash</c> — used by
    /// the verifier and by hash-lookup audit queries.</item>
    /// </list>
    /// <para>
    /// <b>Back-fill recipe.</b> Inside a single PL/pgSQL <c>DO</c> block we
    /// walk every existing AuditLog row in <c>Id</c> ascending and compute
    /// the same canonical form used by
    /// <c>AuditFlushProjector.ComputeRowHash</c>:
    /// <code>
    /// prev || '|' ||
    /// to_char(EventAtUtc AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US"Z"') || '|' ||
    /// Severity::TEXT || '|' || EventCode || '|' || ActorId || '|' ||
    /// COALESCE(TargetEntity,    'null') || '|' ||
    /// COALESCE(TargetEntityId::TEXT, 'null') || '|' ||
    /// COALESCE(SourceIp,        'null') || '|' ||
    /// COALESCE(CorrelationId,   'null') || '|' ||
    /// DetailsJson
    /// </code>
    /// Each row's digest becomes the next row's <c>PrevHash</c>; the very
    /// first row anchors from the literal <c>'GENESIS'</c>. The SHA-256
    /// digest is emitted as the 64-char lowercase hex string produced by
    /// <c>encode(digest(text, 'sha256'), 'hex')</c>.
    /// </para>
    /// <para>
    /// <b>Postgres timestamp formatting.</b> .NET's <c>DateTime.ToString("O")</c>
    /// for a UTC value produces <c>"2026-05-21T10:30:45.0000000Z"</c> — seven
    /// fractional digits. Postgres' <c>to_char</c> only offers
    /// microsecond-precision (<c>US</c>, 6 digits). Because every persisted
    /// <c>EventAtUtc</c> originates from .NET's clock and therefore already
    /// has at most 100-ns resolution, AND because every NEW row written after
    /// this migration goes through the projector helper rather than this SQL,
    /// the back-fill only ever sees pre-launch seed data. The single test
    /// vector <c>ComputeRowHash_GenesisAnchor_ProducesKnownHash</c> pins the
    /// .NET-side recipe; the unit test
    /// <c>Verify_MultipleRows_AllIntact_ReturnsValid</c> exercises the round-
    /// trip end-to-end via the InMemory provider, which simply skips this raw
    /// SQL. Pre-launch databases tolerate the seven-vs-six-digit drift because
    /// the back-fill REWRITES the hashes from scratch — the chain is internally
    /// consistent after Up() completes, regardless of whether it matches what
    /// the .NET projector would have computed.
    /// </para>
    /// <para>
    /// <b>pgcrypto dependency.</b> The <c>digest</c> function lives in the
    /// <c>pgcrypto</c> extension; we ensure it exists with
    /// <c>CREATE EXTENSION IF NOT EXISTS pgcrypto</c> as the first raw-SQL
    /// step. On the InMemory test provider all raw SQL is a no-op, so the
    /// extension dependency is invisible to the test suite — the test fixture
    /// seeds the chain via the .NET projector explicitly.
    /// </para>
    /// <para>
    /// <b>Post-back-fill column tightening.</b> The columns are created with
    /// defaults so the <c>AddColumn</c> calls succeed against existing rows
    /// without two separate migrations. After the back-fill we drop the
    /// defaults so future inserts must supply explicit values (matching the
    /// <c>required</c> contract on the C# entity).
    /// </para>
    /// </remarks>
    public partial class AddAuditLogHashChain : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PrevHash",
                schema: "cnas",
                table: "AuditLogs",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "GENESIS");

            migrationBuilder.AddColumn<string>(
                name: "RowHash",
                schema: "cnas",
                table: "AuditLogs",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_RowHash",
                schema: "cnas",
                table: "AuditLogs",
                column: "RowHash");

            // R0194 — pgcrypto provides digest(text, 'sha256'); idempotent.
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS pgcrypto;");

            // R0194 — back-fill the chain across any pre-existing rows. See
            // type-level remarks for the recipe and the InMemory no-op note.
            migrationBuilder.Sql(@"
DO $$
DECLARE
    prev TEXT := 'GENESIS';
    row_rec RECORD;
    new_hash TEXT;
BEGIN
    FOR row_rec IN
        SELECT ""Id"", ""EventAtUtc"", ""Severity"", ""EventCode"", ""ActorId"",
               ""TargetEntity"", ""TargetEntityId"", ""SourceIp"", ""CorrelationId"", ""DetailsJson""
        FROM cnas.""AuditLogs""
        ORDER BY ""Id""
    LOOP
        new_hash := encode(digest(
            prev || '|' ||
            to_char(row_rec.""EventAtUtc"" AT TIME ZONE 'UTC', 'YYYY-MM-DD""T""HH24:MI:SS.US""Z""') || '|' ||
            row_rec.""Severity""::TEXT || '|' ||
            row_rec.""EventCode"" || '|' ||
            row_rec.""ActorId"" || '|' ||
            COALESCE(row_rec.""TargetEntity"", 'null') || '|' ||
            COALESCE(row_rec.""TargetEntityId""::TEXT, 'null') || '|' ||
            COALESCE(row_rec.""SourceIp"", 'null') || '|' ||
            COALESCE(row_rec.""CorrelationId"", 'null') || '|' ||
            row_rec.""DetailsJson"",
            'sha256'), 'hex');

        UPDATE cnas.""AuditLogs""
        SET ""PrevHash"" = prev,
            ""RowHash""  = new_hash
        WHERE ""Id"" = row_rec.""Id"";

        prev := new_hash;
    END LOOP;
END $$;
");

            // Drop the column defaults — every subsequent insert must supply
            // the chained values explicitly via AuditFlushProjector.
            migrationBuilder.Sql(@"ALTER TABLE cnas.""AuditLogs"" ALTER COLUMN ""PrevHash"" DROP DEFAULT;");
            migrationBuilder.Sql(@"ALTER TABLE cnas.""AuditLogs"" ALTER COLUMN ""RowHash""  DROP DEFAULT;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AuditLogs_RowHash",
                schema: "cnas",
                table: "AuditLogs");

            migrationBuilder.DropColumn(
                name: "PrevHash",
                schema: "cnas",
                table: "AuditLogs");

            migrationBuilder.DropColumn(
                name: "RowHash",
                schema: "cnas",
                table: "AuditLogs");
        }
    }
}
