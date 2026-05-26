using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMigrationRegistry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MigrationBatches",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RunId = table.Column<long>(type: "bigint", nullable: false),
                    BatchOrdinal = table.Column<int>(type: "integer", nullable: false),
                    RowsInBatch = table.Column<int>(type: "integer", nullable: false),
                    RowsImported = table.Column<int>(type: "integer", nullable: false),
                    RowsUpdated = table.Column<int>(type: "integer", nullable: false),
                    RowsSkipped = table.Column<int>(type: "integer", nullable: false),
                    RowsFailed = table.Column<int>(type: "integer", nullable: false),
                    DurationMs = table.Column<long>(type: "bigint", nullable: false),
                    ProcessedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MigrationBatches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MigrationFindings",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RunId = table.Column<long>(type: "bigint", nullable: false),
                    BatchOrdinal = table.Column<int>(type: "integer", nullable: false),
                    RowOrdinalInBatch = table.Column<int>(type: "integer", nullable: false),
                    Severity = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    FindingCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    SourceFingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Acknowledged = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
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
                    table.PrimaryKey("PK_MigrationFindings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MigrationPlans",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PlanCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Title = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    SourceKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    TargetEntityName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    MappingDescriptorJson = table.Column<string>(type: "character varying(16384)", maxLength: 16384, nullable: true),
                    BatchSize = table.Column<int>(type: "integer", nullable: false, defaultValue: 1000),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RegisteredByUserId = table.Column<long>(type: "bigint", nullable: false),
                    ApprovedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    ApprovedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MigrationPlans", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MigrationRuns",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PlanId = table.Column<long>(type: "bigint", nullable: false),
                    TriggerKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TotalSourceRowsSeen = table.Column<long>(type: "bigint", nullable: false),
                    TotalRowsImported = table.Column<long>(type: "bigint", nullable: false),
                    TotalRowsUpdated = table.Column<long>(type: "bigint", nullable: false),
                    TotalRowsSkipped = table.Column<long>(type: "bigint", nullable: false),
                    TotalRowsFailed = table.Column<long>(type: "bigint", nullable: false),
                    FailureReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    IsDryRun = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MigrationRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MigrationStagingRows",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RunId = table.Column<long>(type: "bigint", nullable: false),
                    BatchOrdinal = table.Column<int>(type: "integer", nullable: false),
                    RowOrdinalInBatch = table.Column<int>(type: "integer", nullable: false),
                    TargetEntityName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    TargetEntityKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    MappedFieldsJson = table.Column<string>(type: "character varying(16384)", maxLength: 16384, nullable: false),
                    SourceFingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    IsCommitted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    CommittedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MigrationStagingRows", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ReconciliationReports",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RunId = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SourceRowCount = table.Column<long>(type: "bigint", nullable: false),
                    TargetRowCount = table.Column<long>(type: "bigint", nullable: false),
                    MissingInTargetCount = table.Column<long>(type: "bigint", nullable: false),
                    UnexpectedInTargetCount = table.Column<long>(type: "bigint", nullable: false),
                    ChecksumMatchRate = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    DiscrepancyDetailsJson = table.Column<string>(type: "character varying(16384)", maxLength: 16384, nullable: true),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReconciliationReports", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MigrationBatches_CreatedAtUtc",
                schema: "cnas",
                table: "MigrationBatches",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_MigrationBatches_IsActive",
                schema: "cnas",
                table: "MigrationBatches",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "UX_MigrationBatches_RunId_BatchOrdinal",
                schema: "cnas",
                table: "MigrationBatches",
                columns: new[] { "RunId", "BatchOrdinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MigrationFindings_CreatedAtUtc",
                schema: "cnas",
                table: "MigrationFindings",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_MigrationFindings_FindingCode",
                schema: "cnas",
                table: "MigrationFindings",
                column: "FindingCode");

            migrationBuilder.CreateIndex(
                name: "IX_MigrationFindings_IsActive",
                schema: "cnas",
                table: "MigrationFindings",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_MigrationFindings_RunId_BatchOrdinal",
                schema: "cnas",
                table: "MigrationFindings",
                columns: new[] { "RunId", "BatchOrdinal" });

            migrationBuilder.CreateIndex(
                name: "IX_MigrationFindings_Severity_Acknowledged",
                schema: "cnas",
                table: "MigrationFindings",
                columns: new[] { "Severity", "Acknowledged" });

            migrationBuilder.CreateIndex(
                name: "IX_MigrationPlans_CreatedAtUtc",
                schema: "cnas",
                table: "MigrationPlans",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_MigrationPlans_IsActive",
                schema: "cnas",
                table: "MigrationPlans",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_MigrationPlans_Status_TargetEntityName",
                schema: "cnas",
                table: "MigrationPlans",
                columns: new[] { "Status", "TargetEntityName" });

            migrationBuilder.CreateIndex(
                name: "UX_MigrationPlans_PlanCode",
                schema: "cnas",
                table: "MigrationPlans",
                column: "PlanCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MigrationRuns_CreatedAtUtc",
                schema: "cnas",
                table: "MigrationRuns",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_MigrationRuns_IsActive",
                schema: "cnas",
                table: "MigrationRuns",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_MigrationRuns_StartedAt_Desc",
                schema: "cnas",
                table: "MigrationRuns",
                column: "StartedAt",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_MigrationRuns_Status_PlanId",
                schema: "cnas",
                table: "MigrationRuns",
                columns: new[] { "Status", "PlanId" });

            migrationBuilder.CreateIndex(
                name: "IX_MigrationStagingRows_CreatedAtUtc",
                schema: "cnas",
                table: "MigrationStagingRows",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_MigrationStagingRows_IsActive",
                schema: "cnas",
                table: "MigrationStagingRows",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_MigrationStagingRows_TargetEntity_IsCommitted",
                schema: "cnas",
                table: "MigrationStagingRows",
                columns: new[] { "TargetEntityName", "IsCommitted" });

            migrationBuilder.CreateIndex(
                name: "IX_MigrationStagingRows_TargetEntityKey",
                schema: "cnas",
                table: "MigrationStagingRows",
                column: "TargetEntityKey");

            migrationBuilder.CreateIndex(
                name: "UX_MigrationStagingRows_RunId_BatchOrdinal_RowOrdinal",
                schema: "cnas",
                table: "MigrationStagingRows",
                columns: new[] { "RunId", "BatchOrdinal", "RowOrdinalInBatch" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReconciliationReports_CreatedAtUtc",
                schema: "cnas",
                table: "ReconciliationReports",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ReconciliationReports_IsActive",
                schema: "cnas",
                table: "ReconciliationReports",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "UX_ReconciliationReports_RunId",
                schema: "cnas",
                table: "ReconciliationReports",
                column: "RunId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MigrationBatches",
                schema: "cnas");

            migrationBuilder.DropTable(
                name: "MigrationFindings",
                schema: "cnas");

            migrationBuilder.DropTable(
                name: "MigrationPlans",
                schema: "cnas");

            migrationBuilder.DropTable(
                name: "MigrationRuns",
                schema: "cnas");

            migrationBuilder.DropTable(
                name: "MigrationStagingRows",
                schema: "cnas");

            migrationBuilder.DropTable(
                name: "ReconciliationReports",
                schema: "cnas");
        }
    }
}
