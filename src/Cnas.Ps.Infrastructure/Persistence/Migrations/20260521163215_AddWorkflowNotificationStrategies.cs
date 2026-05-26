using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// R0128 / R0173 / CF 16.14 + CF 22.04 — creates the
    /// <c>cnas.WorkflowNotificationStrategies</c> table backing the per-workflow
    /// notification-strategy registry consulted by the workflow orchestrator at
    /// dispatch time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>No seed strategy.</b> Strategies are bound by foreign key to
    /// <c>WorkflowDefinitions.Id</c>; the surrogate key is assigned at first
    /// save by the EF Core identity column and varies per environment. There is
    /// no stable workflow id we can safely reference in a static
    /// <c>INSERT</c> statement, so the migration intentionally seeds NO rows —
    /// the table comes online empty and the orchestrator falls back to its
    /// legacy default behaviour until operators provision strategies via the
    /// admin REST surface. This decision is documented in the iteration report
    /// alongside R0128 / R0173.
    /// </para>
    /// <para>
    /// <b>Indexes.</b> The composite natural-key UNIQUE on
    /// (<c>WorkflowDefinitionId</c>, <c>EventCode</c>) backs the resolver's
    /// upsert + the CRUD service's <c>GetByEventAsync</c>. The
    /// <c>AuditableEntityConfiguration</c> contributes <c>IsActive</c> +
    /// <c>CreatedAtUtc</c> indexes used by the admin list view.
    /// </para>
    /// <para>
    /// <b>Down migration.</b> Drops the table cleanly. There are no foreign keys
    /// in the database schema; the WorkflowDefinitionId is enforced at the
    /// application layer rather than via a DB constraint (matching the
    /// codebase's broader pattern around workflow-related FK references).
    /// </para>
    /// </remarks>
    public partial class AddWorkflowNotificationStrategies : Migration
    {
        /// <summary>Creates the table + indexes.</summary>
        /// <param name="migrationBuilder">EF Core migration builder.</param>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.CreateTable(
                name: "WorkflowNotificationStrategies",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    WorkflowDefinitionId = table.Column<long>(type: "bigint", nullable: false),
                    EventCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    Channels = table.Column<string>(type: "jsonb", nullable: false),
                    RecipientRoles = table.Column<string>(type: "jsonb", nullable: false),
                    TemplateCodeOverride = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    QuietHoursStartLocalMinute = table.Column<int>(type: "integer", nullable: true),
                    QuietHoursEndLocalMinute = table.Column<int>(type: "integer", nullable: true),
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
                    table.PrimaryKey("PK_WorkflowNotificationStrategies", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowNotificationStrategies_CreatedAtUtc",
                schema: "cnas",
                table: "WorkflowNotificationStrategies",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowNotificationStrategies_IsActive",
                schema: "cnas",
                table: "WorkflowNotificationStrategies",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowNotificationStrategies_WorkflowDefinitionId_EventCo~",
                schema: "cnas",
                table: "WorkflowNotificationStrategies",
                columns: new[] { "WorkflowDefinitionId", "EventCode" },
                unique: true);
        }

        /// <summary>Drops the R0128 / R0173 table cleanly.</summary>
        /// <param name="migrationBuilder">EF Core migration builder.</param>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropTable(
                name: "WorkflowNotificationStrategies",
                schema: "cnas");
        }
    }
}
