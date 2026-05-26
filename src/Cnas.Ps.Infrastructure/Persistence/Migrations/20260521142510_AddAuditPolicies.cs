using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// R0182 / SEC 042 — creates the <c>cnas.AuditPolicies</c> table backing the
    /// admin-configurable audit-policy registry consulted by the audit drainer at
    /// flush time. Seeds six canonical policies covering the SEC 042 example matrix
    /// (search, PII read, bulk export, decision draft read, admin role change, read
    /// of the policy table itself).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Seed strategy.</b> The six default policies are inserted with
    /// <c>ON CONFLICT ("Code") DO NOTHING</c> so re-running the migration against a
    /// previously-migrated database is a no-op — an operator who edited a default
    /// policy's threshold / severity will NOT see the edit overwritten on the next
    /// deploy. Mirrors the R0189 idempotent-seed convention.
    /// </para>
    /// <para>
    /// <b>OverrideSeverity ints.</b> The seed rows use the <c>AuditSeverity</c>
    /// enum's int order: <c>0 = Information</c>, <c>1 = Notice</c>,
    /// <c>2 = Sensitive</c>, <c>3 = Critical</c>. Null is "preserve caller severity".
    /// </para>
    /// <para>
    /// <b>Down migration.</b> Drops the table cleanly. There are no foreign keys.
    /// </para>
    /// </remarks>
    public partial class AddAuditPolicies : Migration
    {
        /// <summary>Creates the table + indexes and seeds the six default policies idempotently.</summary>
        /// <param name="migrationBuilder">EF Core migration builder.</param>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.CreateTable(
                name: "AuditPolicies",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Code = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Module = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Screen = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DataCategory = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    EventCodePattern = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    OverrideSeverity = table.Column<int>(type: "integer", nullable: true),
                    SuppressAudit = table.Column<bool>(type: "boolean", nullable: false),
                    ExtraRedactKeys = table.Column<string>(type: "jsonb", nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false, defaultValue: 100),
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
                    table.PrimaryKey("PK_AuditPolicies", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditPolicies_Code",
                schema: "cnas",
                table: "AuditPolicies",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditPolicies_CreatedAtUtc",
                schema: "cnas",
                table: "AuditPolicies",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AuditPolicies_IsActive",
                schema: "cnas",
                table: "AuditPolicies",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_AuditPolicies_Module_Screen_IsEnabled",
                schema: "cnas",
                table: "AuditPolicies",
                columns: new[] { "Module", "Screen", "IsEnabled" });

            // R0182 — seed the six canonical audit policies. ON CONFLICT protects
            // operator edits: once a policy exists, re-running the migration does
            // NOT overwrite operator-tuned severity / suppression / redaction.
            // OverrideSeverity ints: 0=Information, 1=Notice, 2=Sensitive, 3=Critical.

            // 1) Search listing — no-op / Information passthrough (kept on the books
            //    so the audit explorer surfaces "we considered this event" with the
            //    policy's metadata).
            migrationBuilder.Sql(
                "INSERT INTO cnas.\"AuditPolicies\" "
                + "(\"Code\", \"Module\", \"Screen\", \"DataCategory\", \"EventCodePattern\", "
                + "\"OverrideSeverity\", \"SuppressAudit\", \"ExtraRedactKeys\", \"Priority\", "
                + "\"IsEnabled\", \"Description\", \"IsActive\", \"CreatedAtUtc\") "
                + "VALUES ('solicitant.view.search', 'Solicitant', 'Search', NULL, "
                + "'^SOLICITANT\\.VIEW\\.SEARCH$', 0, FALSE, '[]'::jsonb, 100, TRUE, "
                + "'R0182 seed — passthrough at Information for routine solicitant search.', "
                + "TRUE, NOW()) "
                + "ON CONFLICT (\"Code\") DO NOTHING;");

            // 2) Solicitant detail PII read — lift to Sensitive, redact IBAN + bank account.
            migrationBuilder.Sql(
                "INSERT INTO cnas.\"AuditPolicies\" "
                + "(\"Code\", \"Module\", \"Screen\", \"DataCategory\", \"EventCodePattern\", "
                + "\"OverrideSeverity\", \"SuppressAudit\", \"ExtraRedactKeys\", \"Priority\", "
                + "\"IsEnabled\", \"Description\", \"IsActive\", \"CreatedAtUtc\") "
                + "VALUES ('solicitant.view.detail.pii', 'Solicitant', 'Detail', 'PII', "
                + "'^SOLICITANT\\.VIEW\\.DETAIL\\.PII$', 2, FALSE, "
                + "'[\"iban\",\"bankAccount\"]'::jsonb, 100, TRUE, "
                + "'R0182 seed — lifts solicitant PII reads to Sensitive and extends redaction.', "
                + "TRUE, NOW()) "
                + "ON CONFLICT (\"Code\") DO NOTHING;");

            // 3) Bulk export of cereri — Critical, never suppressed.
            migrationBuilder.Sql(
                "INSERT INTO cnas.\"AuditPolicies\" "
                + "(\"Code\", \"Module\", \"Screen\", \"DataCategory\", \"EventCodePattern\", "
                + "\"OverrideSeverity\", \"SuppressAudit\", \"ExtraRedactKeys\", \"Priority\", "
                + "\"IsEnabled\", \"Description\", \"IsActive\", \"CreatedAtUtc\") "
                + "VALUES ('cerere.bulk.export', 'Cerere', 'Export', NULL, "
                + "'^CERERE\\.BULK\\.EXPORT$', 3, FALSE, '[]'::jsonb, 100, TRUE, "
                + "'R0182 seed — bulk export of applications is always Critical.', "
                + "TRUE, NOW()) "
                + "ON CONFLICT (\"Code\") DO NOTHING;");

            // 4) Decision draft read — Notice (an operator looking at a not-yet-final decision).
            migrationBuilder.Sql(
                "INSERT INTO cnas.\"AuditPolicies\" "
                + "(\"Code\", \"Module\", \"Screen\", \"DataCategory\", \"EventCodePattern\", "
                + "\"OverrideSeverity\", \"SuppressAudit\", \"ExtraRedactKeys\", \"Priority\", "
                + "\"IsEnabled\", \"Description\", \"IsActive\", \"CreatedAtUtc\") "
                + "VALUES ('decision.draft.read', 'Decision', 'Draft', NULL, "
                + "'^DECISION\\.DRAFT\\.READ$', 1, FALSE, '[]'::jsonb, 100, TRUE, "
                + "'R0182 seed — reads of decision drafts emit at Notice.', "
                + "TRUE, NOW()) "
                + "ON CONFLICT (\"Code\") DO NOTHING;");

            // 5) Admin role change — Critical, matches ADMIN_ELEVATION patterns.
            migrationBuilder.Sql(
                "INSERT INTO cnas.\"AuditPolicies\" "
                + "(\"Code\", \"Module\", \"Screen\", \"DataCategory\", \"EventCodePattern\", "
                + "\"OverrideSeverity\", \"SuppressAudit\", \"ExtraRedactKeys\", \"Priority\", "
                + "\"IsEnabled\", \"Description\", \"IsActive\", \"CreatedAtUtc\") "
                + "VALUES ('usermgmt.role.change', 'UserMgmt', 'Role', NULL, "
                + "'^USER\\.ROLE_GRANT\\..+$', 3, FALSE, '[]'::jsonb, 100, TRUE, "
                + "'R0182 seed — role grants always emit at Critical, matching the ADMIN_ELEVATION alert.', "
                + "TRUE, NOW()) "
                + "ON CONFLICT (\"Code\") DO NOTHING;");

            // 6) Audit policy read — read-the-policy-table is itself audited at Information.
            migrationBuilder.Sql(
                "INSERT INTO cnas.\"AuditPolicies\" "
                + "(\"Code\", \"Module\", \"Screen\", \"DataCategory\", \"EventCodePattern\", "
                + "\"OverrideSeverity\", \"SuppressAudit\", \"ExtraRedactKeys\", \"Priority\", "
                + "\"IsEnabled\", \"Description\", \"IsActive\", \"CreatedAtUtc\") "
                + "VALUES ('audit.policy.read', 'AuditPolicy', 'List', NULL, "
                + "'^AUDIT\\.POLICY\\.READ$', 0, FALSE, '[]'::jsonb, 100, TRUE, "
                + "'R0182 seed — listing the audit-policy table is recorded at Information.', "
                + "TRUE, NOW()) "
                + "ON CONFLICT (\"Code\") DO NOTHING;");
        }

        /// <summary>Drops the R0182 table cleanly.</summary>
        /// <param name="migrationBuilder">EF Core migration builder.</param>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropTable(
                name: "AuditPolicies",
                schema: "cnas");
        }
    }
}
