using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Creates the <c>cnas.WorkflowDefinitions</c> table that backs
    /// <see cref="Cnas.Ps.Core.Domain.WorkflowDefinition"/>. UC16 (configurez flux de
    /// lucru) persists workflow JSON payloads here; the table replaces the previous
    /// sentinel-failure stub on <c>IWorkflowConfigurationService</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Versioning model.</b> Each save of a workflow code inserts a new row with
    /// <c>Version</c> = previous + 1 and <c>IsCurrent = true</c>; the previous current
    /// row is updated in the same transaction to <c>IsCurrent = false</c>. Old rows are
    /// never deleted, so a complete audit history is queryable. The runtime engine
    /// consumes the row whose <c>IsCurrent</c> is true.
    /// </para>
    /// <para>
    /// Two domain-specific indexes are created:
    /// <list type="bullet">
    ///   <item><description><c>UNIQUE (Code, Version)</c> — natural key; protects against double-publish bugs.</description></item>
    ///   <item><description><c>(Code, IsCurrent) WHERE IsCurrent = true</c> — partial index serving the GetDefinition lookup.</description></item>
    /// </list>
    /// The standard <c>(IsActive)</c> and <c>(CreatedAtUtc)</c> indexes inherited from
    /// <see cref="Cnas.Ps.Infrastructure.Persistence.Configurations.AuditableEntityConfiguration{TEntity}"/>
    /// are also created.
    /// </para>
    /// <para>
    /// Down migration drops the table cleanly — there are no foreign keys into or out
    /// of this row because the workflow definitions are referenced by code (a string),
    /// not by surrogate key.
    /// </para>
    /// </remarks>
    public partial class AddWorkflowDefinitionsTable : Migration
    {
        /// <summary>
        /// Creates <c>cnas.WorkflowDefinitions</c> with its four indexes (natural-key
        /// unique, partial-current lookup, soft-delete, audit-timestamp).
        /// </summary>
        /// <param name="migrationBuilder">EF Core migration builder.</param>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WorkflowDefinitions",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    DefinitionJson = table.Column<string>(type: "text", nullable: false),
                    IsCurrent = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowDefinitions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowDefinitions_Code_IsCurrent",
                schema: "cnas",
                table: "WorkflowDefinitions",
                columns: new[] { "Code", "IsCurrent" },
                filter: "\"IsCurrent\" = true");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowDefinitions_Code_Version",
                schema: "cnas",
                table: "WorkflowDefinitions",
                columns: new[] { "Code", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowDefinitions_CreatedAtUtc",
                schema: "cnas",
                table: "WorkflowDefinitions",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowDefinitions_IsActive",
                schema: "cnas",
                table: "WorkflowDefinitions",
                column: "IsActive");
        }

        /// <summary>Drops <c>cnas.WorkflowDefinitions</c> and all its indexes.</summary>
        /// <param name="migrationBuilder">EF Core migration builder.</param>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkflowDefinitions",
                schema: "cnas");
        }
    }
}
