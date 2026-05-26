using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// R0183 / SEC 043 — creates the <c>cnas.AuditFieldPolicies</c> table backing the
    /// admin-configurable per-entity field-change policy registry consulted by the
    /// diff writer before emitting an audit row. Seeds three canonical policies
    /// covering the SEC 043 example matrix (<c>Solicitant</c>,
    /// <c>ServiceApplication</c>, <c>UserProfile</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Seed strategy.</b> The three default policies are inserted with
    /// <c>ON CONFLICT ("EntityType") DO NOTHING</c> so re-running the migration
    /// against a previously-migrated database is a no-op — an operator who edited
    /// a default policy's tracked-fields will NOT see the edit overwritten on the
    /// next deploy. Mirrors the R0182 / R0189 idempotent-seed convention.
    /// </para>
    /// <para>
    /// <b>Severity ints.</b> The seed rows use the <c>AuditSeverity</c> enum's int
    /// order: <c>0 = Information</c>, <c>1 = Notice</c>, <c>2 = Sensitive</c>,
    /// <c>3 = Critical</c>.
    /// </para>
    /// <para>
    /// <b>Down migration.</b> Drops the table cleanly. There are no foreign keys.
    /// </para>
    /// </remarks>
    public partial class AddAuditFieldPolicies : Migration
    {
        /// <summary>Creates the table + indexes and seeds the three default policies idempotently.</summary>
        /// <param name="migrationBuilder">EF Core migration builder.</param>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.CreateTable(
                name: "AuditFieldPolicies",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EntityType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TrackedFields = table.Column<string>(type: "jsonb", nullable: false),
                    SuppressedFields = table.Column<string>(type: "jsonb", nullable: false),
                    RequireAnyChange = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    Severity = table.Column<int>(type: "integer", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    Description = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditFieldPolicies", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditFieldPolicies_CreatedAtUtc",
                schema: "cnas",
                table: "AuditFieldPolicies",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AuditFieldPolicies_EntityType",
                schema: "cnas",
                table: "AuditFieldPolicies",
                column: "EntityType",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditFieldPolicies_IsActive",
                schema: "cnas",
                table: "AuditFieldPolicies",
                column: "IsActive");

            // R0183 — seed the three canonical field policies. ON CONFLICT protects
            // operator edits: once a policy exists, re-running the migration does
            // NOT overwrite operator-tuned tracked / suppressed / severity fields.
            // Severity ints: 0=Information, 1=Notice, 2=Sensitive, 3=Critical.

            // 1) Solicitant — track display contact fields; redact national-id columns.
            migrationBuilder.Sql(
                "INSERT INTO cnas.\"AuditFieldPolicies\" "
                + "(\"EntityType\", \"TrackedFields\", \"SuppressedFields\", \"RequireAnyChange\", "
                + "\"Severity\", \"IsEnabled\", \"Description\", \"IsActive\", \"CreatedAtUtc\") "
                + "VALUES ('Solicitant', "
                + "'[\"DisplayName\",\"Email\",\"PhoneE164\",\"Address\"]'::jsonb, "
                + "'[\"NationalId\",\"NationalIdHash\"]'::jsonb, "
                + "TRUE, 2, TRUE, "
                + "'R0183 seed — tracks Solicitant display/contact fields at Sensitive; redacts national-id columns.', "
                + "TRUE, NOW()) "
                + "ON CONFLICT (\"EntityType\") DO NOTHING;");

            // 2) ServiceApplication — track lifecycle/assignment columns at Notice.
            migrationBuilder.Sql(
                "INSERT INTO cnas.\"AuditFieldPolicies\" "
                + "(\"EntityType\", \"TrackedFields\", \"SuppressedFields\", \"RequireAnyChange\", "
                + "\"Severity\", \"IsEnabled\", \"Description\", \"IsActive\", \"CreatedAtUtc\") "
                + "VALUES ('ServiceApplication', "
                + "'[\"Status\",\"AssignedUserId\",\"DecisionId\"]'::jsonb, "
                + "'[]'::jsonb, "
                + "TRUE, 1, TRUE, "
                + "'R0183 seed — tracks application lifecycle/assignment transitions at Notice.', "
                + "TRUE, NOW()) "
                + "ON CONFLICT (\"EntityType\") DO NOTHING;");

            // 3) UserProfile — track identity/role columns at Critical; redact password + national-id columns.
            migrationBuilder.Sql(
                "INSERT INTO cnas.\"AuditFieldPolicies\" "
                + "(\"EntityType\", \"TrackedFields\", \"SuppressedFields\", \"RequireAnyChange\", "
                + "\"Severity\", \"IsEnabled\", \"Description\", \"IsActive\", \"CreatedAtUtc\") "
                + "VALUES ('UserProfile', "
                + "'[\"DisplayName\",\"Email\",\"State\",\"Roles\",\"Groups\"]'::jsonb, "
                + "'[\"LocalPasswordHash\",\"NationalId\",\"NationalIdHash\"]'::jsonb, "
                + "TRUE, 3, TRUE, "
                + "'R0183 seed — tracks UserProfile identity/role changes at Critical; redacts password + national-id columns.', "
                + "TRUE, NOW()) "
                + "ON CONFLICT (\"EntityType\") DO NOTHING;");
        }

        /// <summary>Drops the R0183 table cleanly.</summary>
        /// <param name="migrationBuilder">EF Core migration builder.</param>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropTable(
                name: "AuditFieldPolicies",
                schema: "cnas");
        }
    }
}
