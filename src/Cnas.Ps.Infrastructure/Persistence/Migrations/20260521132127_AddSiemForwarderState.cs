using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// R0190 / SEC 049 — creates the <c>cnas.SiemForwarderState</c> singleton-row table
    /// backing <see cref="Cnas.Ps.Core.Domain.SiemForwarderState"/>. The table holds at
    /// most one row per environment (keyed by the literal <c>"default"</c>); that row is
    /// the resume anchor for the SIEM CEF / syslog forwarder background job so a
    /// process restart between two polling cycles never causes audit rows to be
    /// re-emitted to the SIEM.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Singular table name.</b> The table name is intentionally singular —
    /// <c>SiemForwarderState</c>, not <c>SiemForwarderStates</c> — to reflect the
    /// singleton-row semantics. The standard EF / PostgreSQL pluralisation convention
    /// applied elsewhere in the schema does not fit when the "table" carries at most
    /// one logical record.
    /// </para>
    /// <para>
    /// <b>Indexes.</b> Three indexes are declared: the natural-key UNIQUE on
    /// <see cref="Cnas.Ps.Core.Domain.SiemForwarderState.Key"/> (the DB-side safety net
    /// against a racing duplicate seed), plus the standard <c>(IsActive)</c> and
    /// <c>(CreatedAtUtc)</c> indexes contributed by
    /// <see cref="Cnas.Ps.Infrastructure.Persistence.Configurations.AuditableEntityConfiguration{TEntity}"/>.
    /// </para>
    /// <para>
    /// <b>Seed row.</b> The Up migration finishes with an idempotent
    /// <c>INSERT ... ON CONFLICT (Key) DO NOTHING</c> that materialises the singleton
    /// row with <c>Key="default"</c>, <c>LastForwardedAuditId=0</c>,
    /// <c>IsActive=true</c>, and the current UTC timestamp. The ON CONFLICT clause
    /// guarantees the migration can be re-run safely (e.g. when re-applying a sequence
    /// of migrations against a previously-migrated database).
    /// </para>
    /// <para>
    /// <b>Down migration.</b> Drops the table and every dependent index cleanly. There
    /// are no foreign keys into or out of the table, so a rollback is a single
    /// <c>DROP TABLE</c>.
    /// </para>
    /// </remarks>
    public partial class AddSiemForwarderState : Migration
    {
        /// <summary>
        /// Creates <c>cnas.SiemForwarderState</c> with its three indexes (Key UNIQUE,
        /// IsActive, CreatedAtUtc) and seeds the singleton row.
        /// </summary>
        /// <param name="migrationBuilder">EF Core migration builder.</param>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.CreateTable(
                name: "SiemForwarderState",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Key = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    LastForwardedAuditId = table.Column<long>(type: "bigint", nullable: false),
                    LastForwardedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SiemForwarderState", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SiemForwarderState_CreatedAtUtc",
                schema: "cnas",
                table: "SiemForwarderState",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_SiemForwarderState_IsActive",
                schema: "cnas",
                table: "SiemForwarderState",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_SiemForwarderState_Key",
                schema: "cnas",
                table: "SiemForwarderState",
                column: "Key",
                unique: true);

            // R0190 — seed the singleton checkpoint row. Idempotent via ON CONFLICT so
            // re-running migrations against a previously-migrated database is safe.
            // LastForwardedAuditId=0 starts the forwarder at the bottom of the audit
            // table on first fire; the job's strict-greater-than predicate handles the
            // empty-table case naturally (no rows match Id > 0 either).
            migrationBuilder.Sql(
                "INSERT INTO cnas.\"SiemForwarderState\" (\"Key\", \"LastForwardedAuditId\", \"IsActive\", \"CreatedAtUtc\") "
                + "VALUES ('default', 0, TRUE, NOW()) ON CONFLICT (\"Key\") DO NOTHING;");
        }

        /// <summary>Drops <c>cnas.SiemForwarderState</c> and every dependent index.</summary>
        /// <param name="migrationBuilder">EF Core migration builder.</param>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropTable(
                name: "SiemForwarderState",
                schema: "cnas");
        }
    }
}
