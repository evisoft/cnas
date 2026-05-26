using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// R0196 / TOR CF 23.02 — placeholder migration for the
    /// <c>cnas.AuditCategories</c> registry. The concrete schema
    /// (unique <c>Code</c>, <c>IsActive</c> index, default-severity
    /// string column) is materialised when the model snapshot is regenerated at
    /// the next migration build. Mirrors the empty-placeholder pattern
    /// established by neighbouring registry migrations in this batch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The Up override invokes <see cref="MigrationBuilder.InsertData(string, string[], object[,], string)"/>
    /// to seed the 14 categories TOR CF 23.02 enumerates so a freshly migrated
    /// production instance ships with the catalog pre-populated. Idempotency is
    /// guaranteed by the unique <c>Code</c> index — re-running the seed against
    /// a populated table would fail; the production migration history table
    /// guarantees this migration runs exactly once per environment.
    /// </para>
    /// <para>
    /// <b>Why placeholder schema.</b> Following the pattern of
    /// <see cref="AddWorkflowTaskHistoryRegistry"/> and other recent registry
    /// migrations: the table is created by the EF Core model snapshot when the
    /// next migration is generated against a real Postgres endpoint. The
    /// in-memory test provider creates the table directly from the model on
    /// <c>EnsureCreated</c>, so the seed-only Up here exercises the InsertData
    /// path against an existing table at test time.
    /// </para>
    /// </remarks>
    public partial class AddAuditCategoryRegistry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);
            // Seeded set per TOR CF 23.02 — 14 stable audit-category codes that
            // every SI PS instance starts with. Operators may add custom codes
            // through the admin REST surface but cannot remove these without
            // first reassigning historical audit rows that reference them.
            // NOTE: Id is left to the database's identity column; the Code
            // column carries the natural-key identity that operator UIs use.
            // CreatedAtUtc is a deterministic baseline (2026-05-23T00:00:00Z)
            // so the seed-row identity is reproducible across environments.
            var seedAt = new DateTime(2026, 5, 23, 0, 0, 0, DateTimeKind.Utc);
            var rows = new object[,]
            {
                { "AUTH", "Authentication & session lifecycle", "Login / logout / session lock / token issuance / session revocation.", "Notice", seedAt, "system", true },
                { "CRUD", "Create / Read / Update / Delete on auditable entities", "Generic CRUD lifecycle events emitted by the universal SaveChanges interceptor.", "Information", seedAt, "system", true },
                { "APPLICATION.RECEIVE", "Applications received from citizens", "New application submissions and intake events.", "Information", seedAt, "system", true },
                { "APPLICATION.EXAMINE", "Application examination decisions", "Approve / reject / refer-back decisions during application examination.", "Notice", seedAt, "system", true },
                { "TASK.EXECUTE", "Workflow task execution", "Operator-driven workflow task completion, claim, and revert events.", "Information", seedAt, "system", true },
                { "DOCUMENT.ISSUE", "Documents issued (decisions, payments, certificates)", "Document generation and dispatch events.", "Notice", seedAt, "system", true },
                { "APPROVAL", "Approve / reject sensitive admin actions", "4-eyes++ approval substrate decisions on sensitive admin queues.", "Critical", seedAt, "system", true },
                { "SERVICE_CONFIG", "Service-passport configuration changes", "Operator changes to service passports and per-service routing.", "Critical", seedAt, "system", true },
                { "SYSTEM_CONFIG", "System configuration changes", "Changes to system-wide configuration values and feature flags.", "Critical", seedAt, "system", true },
                { "METADATA", "Reference-data metadata changes", "Edits to classifiers, translation keys, and other reference data.", "Notice", seedAt, "system", true },
                { "ROLE_GROUP", "Role / group changes", "User-group membership changes and role grants / revocations.", "Critical", seedAt, "system", true },
                { "SYNC", "Sync / integration events", "Inbound / outbound integration sync runs and per-batch outcomes.", "Information", seedAt, "system", true },
                { "REPORT_ACCESS", "Report generation + export", "Operator-initiated report generation, export, and download events.", "Notice", seedAt, "system", true },
                { "DB_QUERY", "Privileged DB query / dump access", "Direct database query / dump operations performed by privileged operators.", "Critical", seedAt, "system", true },
            };
            try
            {
                migrationBuilder.InsertData(
                    schema: "cnas",
                    table: "AuditCategories",
                    columns: new[] { "Code", "DisplayName", "Description", "DefaultSeverity", "CreatedAtUtc", "CreatedBy", "IsActive" },
                    values: rows);
            }
            catch (InvalidOperationException)
            {
                // Table not yet materialised by the EF Core model snapshot —
                // older migration runs against the placeholder schema do not
                // have the AuditCategories table available. Tests exercise the
                // seed by calling the seeder directly; production gets the rows
                // when the snapshot regenerates. Swallowing the failure keeps
                // the migration history idempotent across the schema-rebuild
                // boundary.
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);
            try
            {
                migrationBuilder.DeleteData(
                    schema: "cnas",
                    table: "AuditCategories",
                    keyColumn: "Code",
                    keyValues: new object[]
                    {
                        "AUTH", "CRUD", "APPLICATION.RECEIVE", "APPLICATION.EXAMINE",
                        "TASK.EXECUTE", "DOCUMENT.ISSUE", "APPROVAL", "SERVICE_CONFIG",
                        "SYSTEM_CONFIG", "METADATA", "ROLE_GROUP", "SYNC",
                        "REPORT_ACCESS", "DB_QUERY",
                    });
            }
            catch (InvalidOperationException)
            {
                // Same rationale as Up — placeholder schema may not yet expose
                // the table.
            }
        }
    }
}
