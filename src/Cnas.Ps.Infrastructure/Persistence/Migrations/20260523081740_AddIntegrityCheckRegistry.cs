using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddIntegrityCheckRegistry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IntegrityCheckRuns",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RunStartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RunCompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TriggerKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    TotalRowsScanned = table.Column<long>(type: "bigint", nullable: false),
                    TotalFindings = table.Column<int>(type: "integer", nullable: false),
                    FindingsBySeverity = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    FailureReason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntegrityCheckRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IntegrityCheckFindings",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RunId = table.Column<long>(type: "bigint", nullable: false),
                    CheckCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Severity = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AggregateName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    AggregateRowId = table.Column<long>(type: "bigint", nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    ExpectedValue = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ActualValue = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    FirstDetectedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Acknowledged = table.Column<bool>(type: "boolean", nullable: false),
                    AcknowledgedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AcknowledgedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    AcknowledgementNote = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntegrityCheckFindings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IntegrityCheckFindings_IntegrityCheckRuns_RunId",
                        column: x => x.RunId,
                        principalSchema: "cnas",
                        principalTable: "IntegrityCheckRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IntegrityCheckFindings_CheckCode_AggregateRowId",
                schema: "cnas",
                table: "IntegrityCheckFindings",
                columns: new[] { "CheckCode", "AggregateRowId" });

            migrationBuilder.CreateIndex(
                name: "IX_IntegrityCheckFindings_CreatedAtUtc",
                schema: "cnas",
                table: "IntegrityCheckFindings",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_IntegrityCheckFindings_FirstDetectedAt",
                schema: "cnas",
                table: "IntegrityCheckFindings",
                column: "FirstDetectedAt",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_IntegrityCheckFindings_IsActive",
                schema: "cnas",
                table: "IntegrityCheckFindings",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_IntegrityCheckFindings_RunId",
                schema: "cnas",
                table: "IntegrityCheckFindings",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_IntegrityCheckFindings_Severity_Acknowledged",
                schema: "cnas",
                table: "IntegrityCheckFindings",
                columns: new[] { "Severity", "Acknowledged" });

            migrationBuilder.CreateIndex(
                name: "IX_IntegrityCheckRuns_CreatedAtUtc",
                schema: "cnas",
                table: "IntegrityCheckRuns",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_IntegrityCheckRuns_IsActive",
                schema: "cnas",
                table: "IntegrityCheckRuns",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_IntegrityCheckRuns_RunStartedAt",
                schema: "cnas",
                table: "IntegrityCheckRuns",
                column: "RunStartedAt",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_IntegrityCheckRuns_Status",
                schema: "cnas",
                table: "IntegrityCheckRuns",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IntegrityCheckFindings",
                schema: "cnas");

            migrationBuilder.DropTable(
                name: "IntegrityCheckRuns",
                schema: "cnas");
        }
    }
}
