using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// R0123 / TOR CF 16.05 — adds the persisted workflow execution graph
    /// (<c>cnas.WorkflowGraphNodes</c> + <c>cnas.WorkflowGraphEdges</c>) and the
    /// graph-anchor columns (<c>NodeCode</c>, <c>ParentSplitTaskId</c>) on
    /// <c>cnas.WorkflowTasks</c>. No seed data — workflow ids are environment-specific
    /// surrogate keys; administrators provision graphs via the admin REST surface.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Reversibility.</b> The Down path drops both new tables cleanly and removes
    /// the two new columns from <c>WorkflowTasks</c>. Workflows that bound a graph or
    /// rows that anchored to a node lose those attributes; pre-existing rows survive
    /// untouched.
    /// </para>
    /// <para>
    /// <b>No FK constraint on ParentSplitTaskId.</b> The column is a self-reference
    /// into <c>WorkflowTasks</c> but is modelled as a plain BIGINT (no FK) to avoid a
    /// cyclic dependency in the model that would complicate EF's metadata graph. The
    /// sargable index on the column is sufficient for the AND-join sibling-completion
    /// query.
    /// </para>
    /// </remarks>
    public partial class AddWorkflowGraphNodesEdges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);
            migrationBuilder.AddColumn<string>(
                name: "NodeCode",
                schema: "cnas",
                table: "WorkflowTasks",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ParentSplitTaskId",
                schema: "cnas",
                table: "WorkflowTasks",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "WorkflowGraphEdges",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    WorkflowDefinitionId = table.Column<long>(type: "bigint", nullable: false),
                    SourceNodeId = table.Column<long>(type: "bigint", nullable: false),
                    TargetNodeId = table.Column<long>(type: "bigint", nullable: false),
                    Label = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    OrderIndex = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowGraphEdges", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowGraphNodes",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    WorkflowDefinitionId = table.Column<long>(type: "bigint", nullable: false),
                    NodeCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    AssigneeRole = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ConditionExpression = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    OrderIndex = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowGraphNodes", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowTasks_ParentSplitTaskId",
                schema: "cnas",
                table: "WorkflowTasks",
                column: "ParentSplitTaskId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowGraphEdges_CreatedAtUtc",
                schema: "cnas",
                table: "WorkflowGraphEdges",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowGraphEdges_IsActive",
                schema: "cnas",
                table: "WorkflowGraphEdges",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowGraphEdges_WorkflowDefinitionId_SourceNodeId",
                schema: "cnas",
                table: "WorkflowGraphEdges",
                columns: new[] { "WorkflowDefinitionId", "SourceNodeId" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowGraphEdges_WorkflowDefinitionId_TargetNodeId",
                schema: "cnas",
                table: "WorkflowGraphEdges",
                columns: new[] { "WorkflowDefinitionId", "TargetNodeId" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowGraphNodes_CreatedAtUtc",
                schema: "cnas",
                table: "WorkflowGraphNodes",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowGraphNodes_IsActive",
                schema: "cnas",
                table: "WorkflowGraphNodes",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowGraphNodes_WorkflowDefinitionId_NodeCode",
                schema: "cnas",
                table: "WorkflowGraphNodes",
                columns: new[] { "WorkflowDefinitionId", "NodeCode" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);
            migrationBuilder.DropTable(
                name: "WorkflowGraphEdges",
                schema: "cnas");

            migrationBuilder.DropTable(
                name: "WorkflowGraphNodes",
                schema: "cnas");

            migrationBuilder.DropIndex(
                name: "IX_WorkflowTasks_ParentSplitTaskId",
                schema: "cnas",
                table: "WorkflowTasks");

            migrationBuilder.DropColumn(
                name: "NodeCode",
                schema: "cnas",
                table: "WorkflowTasks");

            migrationBuilder.DropColumn(
                name: "ParentSplitTaskId",
                schema: "cnas",
                table: "WorkflowTasks");
        }
    }
}
