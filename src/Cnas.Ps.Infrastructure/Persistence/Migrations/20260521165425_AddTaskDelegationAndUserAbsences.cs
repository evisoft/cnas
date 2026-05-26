using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// R0127 / CF 16.11 — per-task reassignment columns on <c>WorkflowTasks</c> plus the
    /// new <c>UserAbsences</c> table that drives absence-based bulk delegation. Two
    /// indexes back the lifecycle-job sweep + the per-user listing/overlap check; a
    /// nullable index on <c>WorkflowTasks.DelegatedFromAbsenceId</c> backs the
    /// completion-time revert query.
    /// </summary>
    /// <remarks>
    /// No back-fill — existing tasks materialise with <c>ReassignmentCount = 0</c>
    /// (default) and <c>OriginalAssigneeUserId = NULL</c>, which is the correct initial
    /// state for "never reassigned". The InMemory test provider does not apply the
    /// default; entity construction is responsible for it.
    /// </remarks>
    public partial class AddTaskDelegationAndUserAbsences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);
            migrationBuilder.AddColumn<long>(
                name: "DelegatedFromAbsenceId",
                schema: "cnas",
                table: "WorkflowTasks",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "OriginalAssigneeUserId",
                schema: "cnas",
                table: "WorkflowTasks",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReassignmentCount",
                schema: "cnas",
                table: "WorkflowTasks",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ReassignmentReason",
                schema: "cnas",
                table: "WorkflowTasks",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "UserAbsences",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserUserId = table.Column<long>(type: "bigint", nullable: false),
                    DelegateUserId = table.Column<long>(type: "bigint", nullable: false),
                    StartDateUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndDateUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ActivatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RoutedTaskCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserAbsences", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowTasks_DelegatedFromAbsenceId",
                schema: "cnas",
                table: "WorkflowTasks",
                column: "DelegatedFromAbsenceId");

            migrationBuilder.CreateIndex(
                name: "IX_UserAbsences_CreatedAtUtc",
                schema: "cnas",
                table: "UserAbsences",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_UserAbsences_IsActive",
                schema: "cnas",
                table: "UserAbsences",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_UserAbsences_StartDateUtc_EndDateUtc_Status",
                schema: "cnas",
                table: "UserAbsences",
                columns: new[] { "StartDateUtc", "EndDateUtc", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_UserAbsences_UserUserId_Status",
                schema: "cnas",
                table: "UserAbsences",
                columns: new[] { "UserUserId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropTable(
                name: "UserAbsences",
                schema: "cnas");

            migrationBuilder.DropIndex(
                name: "IX_WorkflowTasks_DelegatedFromAbsenceId",
                schema: "cnas",
                table: "WorkflowTasks");

            migrationBuilder.DropColumn(
                name: "DelegatedFromAbsenceId",
                schema: "cnas",
                table: "WorkflowTasks");

            migrationBuilder.DropColumn(
                name: "OriginalAssigneeUserId",
                schema: "cnas",
                table: "WorkflowTasks");

            migrationBuilder.DropColumn(
                name: "ReassignmentCount",
                schema: "cnas",
                table: "WorkflowTasks");

            migrationBuilder.DropColumn(
                name: "ReassignmentReason",
                schema: "cnas",
                table: "WorkflowTasks");
        }
    }
}
