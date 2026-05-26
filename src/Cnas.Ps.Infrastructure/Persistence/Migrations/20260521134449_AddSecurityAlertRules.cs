using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// R0189 / SEC 048 — creates the <c>cnas.SecurityAlertRules</c> rule table and the
    /// <c>cnas.SecurityAlertEvaluatorState</c> singleton-row checkpoint table backing the
    /// security-alert evaluator background job. Seeds four default rules covering the
    /// common SEC 048 cases (failed-login burst, account locked, refresh-token reuse,
    /// admin-role escalation) and the singleton evaluator-state row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two tables, two patterns.</b> <c>SecurityAlertRules</c> is a plural table —
    /// operators may add, edit, and disable many rule rows over time (the admin UI
    /// surface that wraps it is deferred to R0193 / R0182). <c>SecurityAlertEvaluatorState</c>
    /// is intentionally singular because it carries at most one row per environment,
    /// keyed by the literal <c>"default"</c>, exactly like R0190's <c>SiemForwarderState</c>.
    /// </para>
    /// <para>
    /// <b>Seed strategy.</b> The four default rules are inserted with
    /// <c>ON CONFLICT ("Code") DO NOTHING</c> so re-running the migration against a
    /// previously-migrated database is a no-op (an operator who edited a default rule's
    /// threshold will NOT see the edit overwritten on the next deploy). The singleton
    /// state row is inserted with <c>ON CONFLICT ("Key") DO NOTHING</c> for the same
    /// reason. Both follow the R0190 idempotent-seed convention.
    /// </para>
    /// <para>
    /// <b>AlertSeverity int mapping.</b> The seed rules use the AuditSeverity enum's
    /// int order — <c>1 = Notice</c> (R0189's "Warning" semantics) and
    /// <c>2 = Sensitive</c> (R0189's "Error" semantics). Three of the four seed rules
    /// fire at severity 1; <c>REFRESH_REUSE_DETECTED</c> bumps to 2 because token reuse
    /// is the security-critical case warranting MLog mirroring (severity Critical) once
    /// operators tune the rule.
    /// </para>
    /// <para>
    /// <b>Regex patterns.</b> Each pattern is anchored (<c>^…$</c>) so it matches the
    /// full <c>AuditLog.EventCode</c> column rather than a substring. The patterns
    /// mirror the stable event-code conventions emitted elsewhere in the codebase —
    /// see <c>USER.LOGIN.FAIL</c>, <c>USER.STATE_CHANGE.*</c>,
    /// <c>REFRESH.REUSE_DETECTED</c>, <c>USER.ROLE_GRANT.*</c> for the producer sites.
    /// </para>
    /// <para>
    /// <b>Down migration.</b> Drops both tables cleanly. There are no foreign keys into
    /// or out of either table.
    /// </para>
    /// </remarks>
    public partial class AddSecurityAlertRules : Migration
    {
        /// <summary>
        /// Creates the two tables with their indexes, then seeds the four default rules
        /// and the singleton evaluator-state row idempotently.
        /// </summary>
        /// <param name="migrationBuilder">EF Core migration builder.</param>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.CreateTable(
                name: "SecurityAlertEvaluatorState",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Key = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    LastEvaluatedAuditId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SecurityAlertEvaluatorState", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SecurityAlertRules",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    EventCodePattern = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    WindowSeconds = table.Column<int>(type: "integer", nullable: false),
                    ThresholdCount = table.Column<int>(type: "integer", nullable: false),
                    AlertSeverity = table.Column<int>(type: "integer", nullable: false),
                    RecipientGroup = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CooldownSeconds = table.Column<int>(type: "integer", nullable: false),
                    LastFiredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SecurityAlertRules", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SecurityAlertEvaluatorState_CreatedAtUtc",
                schema: "cnas",
                table: "SecurityAlertEvaluatorState",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_SecurityAlertEvaluatorState_IsActive",
                schema: "cnas",
                table: "SecurityAlertEvaluatorState",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_SecurityAlertEvaluatorState_Key",
                schema: "cnas",
                table: "SecurityAlertEvaluatorState",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SecurityAlertRules_Code",
                schema: "cnas",
                table: "SecurityAlertRules",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SecurityAlertRules_CreatedAtUtc",
                schema: "cnas",
                table: "SecurityAlertRules",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_SecurityAlertRules_IsActive",
                schema: "cnas",
                table: "SecurityAlertRules",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_SecurityAlertRules_IsActive_Code",
                schema: "cnas",
                table: "SecurityAlertRules",
                columns: new[] { "IsActive", "Code" });

            // R0189 — seed the singleton evaluator-state checkpoint row. Idempotent
            // via ON CONFLICT so re-running migrations against a previously-migrated
            // database is safe. LastEvaluatedAuditId=0 starts the evaluator at the
            // bottom of the audit table on first fire; the job's strict-greater-than
            // predicate handles the empty-table case naturally.
            migrationBuilder.Sql(
                "INSERT INTO cnas.\"SecurityAlertEvaluatorState\" "
                + "(\"Key\", \"LastEvaluatedAuditId\", \"IsActive\", \"CreatedAtUtc\") "
                + "VALUES ('default', 0, TRUE, NOW()) "
                + "ON CONFLICT (\"Key\") DO NOTHING;");

            // R0189 — seed the four default security-alert rules. AlertSeverity ints
            // mirror the AuditSeverity enum (1=Notice, 2=Sensitive). ON CONFLICT
            // protects operator edits — once a rule exists, re-running the migration
            // does NOT overwrite operator-tuned thresholds / windows / cooldowns.
            migrationBuilder.Sql(
                "INSERT INTO cnas.\"SecurityAlertRules\" "
                + "(\"Code\", \"EventCodePattern\", \"WindowSeconds\", \"ThresholdCount\", "
                + "\"AlertSeverity\", \"RecipientGroup\", \"CooldownSeconds\", \"IsActive\", \"CreatedAtUtc\") "
                + "VALUES ('FAILED_LOGIN_BURST', '^USER\\.LOGIN\\.FAIL$', 60, 10, 1, 'cnas-admin', 300, TRUE, NOW()) "
                + "ON CONFLICT (\"Code\") DO NOTHING;");

            migrationBuilder.Sql(
                "INSERT INTO cnas.\"SecurityAlertRules\" "
                + "(\"Code\", \"EventCodePattern\", \"WindowSeconds\", \"ThresholdCount\", "
                + "\"AlertSeverity\", \"RecipientGroup\", \"CooldownSeconds\", \"IsActive\", \"CreatedAtUtc\") "
                + "VALUES ('ACCOUNT_LOCKED', '^USER\\.STATE_CHANGE\\..+\\.Locked$', 60, 1, 1, 'cnas-admin', 60, TRUE, NOW()) "
                + "ON CONFLICT (\"Code\") DO NOTHING;");

            migrationBuilder.Sql(
                "INSERT INTO cnas.\"SecurityAlertRules\" "
                + "(\"Code\", \"EventCodePattern\", \"WindowSeconds\", \"ThresholdCount\", "
                + "\"AlertSeverity\", \"RecipientGroup\", \"CooldownSeconds\", \"IsActive\", \"CreatedAtUtc\") "
                + "VALUES ('REFRESH_REUSE_DETECTED', '^REFRESH\\.REUSE_DETECTED$', 300, 1, 2, 'cnas-tech-admin', 300, TRUE, NOW()) "
                + "ON CONFLICT (\"Code\") DO NOTHING;");

            migrationBuilder.Sql(
                "INSERT INTO cnas.\"SecurityAlertRules\" "
                + "(\"Code\", \"EventCodePattern\", \"WindowSeconds\", \"ThresholdCount\", "
                + "\"AlertSeverity\", \"RecipientGroup\", \"CooldownSeconds\", \"IsActive\", \"CreatedAtUtc\") "
                + "VALUES ('ADMIN_ELEVATION', '^USER\\.ROLE_GRANT\\..+\\.Admin$', 300, 1, 1, 'cnas-tech-admin', 300, TRUE, NOW()) "
                + "ON CONFLICT (\"Code\") DO NOTHING;");
        }

        /// <summary>
        /// Drops both R0189 tables (and their indexes by cascade). There are no foreign
        /// keys to consider.
        /// </summary>
        /// <param name="migrationBuilder">EF Core migration builder.</param>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropTable(
                name: "SecurityAlertEvaluatorState",
                schema: "cnas");

            migrationBuilder.DropTable(
                name: "SecurityAlertRules",
                schema: "cnas");
        }
    }
}
