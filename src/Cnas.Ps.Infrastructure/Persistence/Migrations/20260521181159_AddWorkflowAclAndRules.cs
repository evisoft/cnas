using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// R0124 / R0126 / TOR CF 16.08 + CF 16.10 — adds the workflow-scoped ACL columns
    /// (<c>AllowedRoles</c>, <c>AllowedGroups</c>) and the rule-pack lifecycle-hook
    /// columns (<c>StartRulePackCode</c>, <c>TransitionRulePackCode</c>,
    /// <c>CompletionRulePackCode</c>) to <c>cnas.WorkflowDefinitions</c>; creates the
    /// new <c>cnas.WorkflowStepAcls</c> table for per-step ACL refinements.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>JSONB defaults.</b> <c>AllowedRoles</c> / <c>AllowedGroups</c> are stored as
    /// JSONB arrays of strings. The DB-level default is an empty JSON array literal
    /// <c>'[]'</c> so the back-fill of pre-existing rows is parseable by the value
    /// converter. Note that EF Core generated a literal empty string for the default;
    /// we override it with the canonical JSON empty-array because Postgres rejects
    /// <c>''</c> as a JSONB value.
    /// </para>
    /// <para>
    /// <b>No seed.</b> Step ACLs are bound by foreign key to
    /// <c>WorkflowDefinitions.Id</c> — environment-specific surrogate keys; same
    /// rationale as the R0128 migration's no-seed policy. The table comes online
    /// empty and the resolver short-circuits to "no extra requirement" until
    /// operators provision rows via the admin REST surface.
    /// </para>
    /// <para>
    /// <b>Reversibility.</b> The Down path drops the new table cleanly and removes
    /// the five new columns from <c>WorkflowDefinitions</c>. Workflows that bound
    /// step ACLs lose them; pre-existing rows survive untouched.
    /// </para>
    /// </remarks>
    public partial class AddWorkflowAclAndRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.AddColumn<string>(
                name: "AllowedGroups",
                schema: "cnas",
                table: "WorkflowDefinitions",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'::jsonb");

            migrationBuilder.AddColumn<string>(
                name: "AllowedRoles",
                schema: "cnas",
                table: "WorkflowDefinitions",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'::jsonb");

            migrationBuilder.AddColumn<string>(
                name: "CompletionRulePackCode",
                schema: "cnas",
                table: "WorkflowDefinitions",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StartRulePackCode",
                schema: "cnas",
                table: "WorkflowDefinitions",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TransitionRulePackCode",
                schema: "cnas",
                table: "WorkflowDefinitions",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "WorkflowStepAcls",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    WorkflowDefinitionId = table.Column<long>(type: "bigint", nullable: false),
                    StepCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RequiredRoles = table.Column<string>(type: "jsonb", nullable: false),
                    RequiredGroups = table.Column<string>(type: "jsonb", nullable: false),
                    RequiredPermission = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
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
                    table.PrimaryKey("PK_WorkflowStepAcls", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowStepAcls_CreatedAtUtc",
                schema: "cnas",
                table: "WorkflowStepAcls",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowStepAcls_IsActive",
                schema: "cnas",
                table: "WorkflowStepAcls",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowStepAcls_WorkflowDefinitionId_StepCode",
                schema: "cnas",
                table: "WorkflowStepAcls",
                columns: new[] { "WorkflowDefinitionId", "StepCode" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropTable(
                name: "WorkflowStepAcls",
                schema: "cnas");

            migrationBuilder.DropColumn(
                name: "AllowedGroups",
                schema: "cnas",
                table: "WorkflowDefinitions");

            migrationBuilder.DropColumn(
                name: "AllowedRoles",
                schema: "cnas",
                table: "WorkflowDefinitions");

            migrationBuilder.DropColumn(
                name: "CompletionRulePackCode",
                schema: "cnas",
                table: "WorkflowDefinitions");

            migrationBuilder.DropColumn(
                name: "StartRulePackCode",
                schema: "cnas",
                table: "WorkflowDefinitions");

            migrationBuilder.DropColumn(
                name: "TransitionRulePackCode",
                schema: "cnas",
                table: "WorkflowDefinitions");
        }
    }
}
