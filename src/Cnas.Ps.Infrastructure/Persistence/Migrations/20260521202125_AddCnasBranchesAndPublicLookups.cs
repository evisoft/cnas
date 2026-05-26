using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// R0511 / R0512 / R0513 / TOR CF 02.01 — creates the
    /// <c>cnas.CnasBranches</c> table that backs the anonymous online-appointment
    /// directory and seeds five default CNAS regional branches.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Schema changes (Up).</b> Creates the <c>CnasBranches</c> table with a
    /// unique index on <c>Code</c> + secondary indexes on <c>IsActive</c>,
    /// <c>CreatedAtUtc</c>, <c>Name</c>. The table participates in the standard
    /// auditable-entity convention (xmin concurrency token, soft-delete via
    /// <c>IsActive</c>).
    /// </para>
    /// <para>
    /// <b>Seed data.</b> Inserts five hand-curated default branches (Chișinău
    /// Centru, Bălți, Cahul, Comrat, Edineț) via <c>ON CONFLICT (Code) DO
    /// NOTHING</c> so re-running the migration against a previously-migrated
    /// database is a no-op — operator edits to a seeded row will NOT be
    /// overwritten on the next deploy. Follows the R0190 idempotent-seed
    /// convention used by <c>AddSecurityAlertRules</c>.
    /// </para>
    /// <para>
    /// <b>No new columns on existing tables.</b> R0511 reads from PCCM via
    /// MConnect (no local schema) and R0513 reads from <c>Solicitant</c> +
    /// <c>InsuredPerson</c> using the existing <c>IdnpHash</c> / <c>BirthDate</c>
    /// columns. Only the new <c>CnasBranches</c> table is required.
    /// </para>
    /// <para>
    /// <b>Down.</b> Drops the <c>CnasBranches</c> table (and indexes by
    /// cascade). Safe — the table carries no foreign-key dependencies.
    /// </para>
    /// </remarks>
    public partial class AddCnasBranchesAndPublicLookups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.CreateTable(
                name: "CnasBranches",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    City = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Address = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    Phone = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    OnlineSchedulingUrlTemplate = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CnasBranches", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CnasBranches_Code",
                schema: "cnas",
                table: "CnasBranches",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CnasBranches_CreatedAtUtc",
                schema: "cnas",
                table: "CnasBranches",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_CnasBranches_IsActive",
                schema: "cnas",
                table: "CnasBranches",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_CnasBranches_Name",
                schema: "cnas",
                table: "CnasBranches",
                column: "Name");

            // R0512 — seed five default CNAS regional branches. Idempotent via
            // ON CONFLICT (Code) DO NOTHING so re-running the migration against a
            // previously-migrated database is a no-op. Operator edits to a
            // seeded row will NOT be overwritten on the next deploy. Follows
            // the R0190 idempotent-seed convention used by AddSecurityAlertRules.
            migrationBuilder.Sql(
                "INSERT INTO cnas.\"CnasBranches\" (\"Code\", \"Name\", \"City\", \"Address\", \"Phone\", \"IsActive\", \"CreatedAtUtc\") "
                + "VALUES ('BALTI', 'CNAS Bălți', 'Bălți', 'Strada Independenței 1', '+37323122222', TRUE, NOW()) "
                + "ON CONFLICT (\"Code\") DO NOTHING;");

            migrationBuilder.Sql(
                "INSERT INTO cnas.\"CnasBranches\" (\"Code\", \"Name\", \"City\", \"Address\", \"Phone\", \"IsActive\", \"CreatedAtUtc\") "
                + "VALUES ('CAHUL', 'CNAS Cahul', 'Cahul', 'Strada Ștefan cel Mare 12', '+37329933333', TRUE, NOW()) "
                + "ON CONFLICT (\"Code\") DO NOTHING;");

            migrationBuilder.Sql(
                "INSERT INTO cnas.\"CnasBranches\" (\"Code\", \"Name\", \"City\", \"Address\", \"Phone\", \"IsActive\", \"CreatedAtUtc\") "
                + "VALUES ('CHISINAU-CENTRU', 'CNAS Chișinău Centru', 'Chișinău', 'Strada Gheorghe Tudor 3', '+37322257777', TRUE, NOW()) "
                + "ON CONFLICT (\"Code\") DO NOTHING;");

            migrationBuilder.Sql(
                "INSERT INTO cnas.\"CnasBranches\" (\"Code\", \"Name\", \"City\", \"Address\", \"Phone\", \"IsActive\", \"CreatedAtUtc\") "
                + "VALUES ('COMRAT', 'CNAS Comrat', 'Comrat', 'Strada Lenin 109', '+37329844444', TRUE, NOW()) "
                + "ON CONFLICT (\"Code\") DO NOTHING;");

            migrationBuilder.Sql(
                "INSERT INTO cnas.\"CnasBranches\" (\"Code\", \"Name\", \"City\", \"Address\", \"Phone\", \"IsActive\", \"CreatedAtUtc\") "
                + "VALUES ('EDINET', 'CNAS Edineț', 'Edineț', 'Strada Independenței 75', '+37324655555', TRUE, NOW()) "
                + "ON CONFLICT (\"Code\") DO NOTHING;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropTable(
                name: "CnasBranches",
                schema: "cnas");
        }
    }
}
