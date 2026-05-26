using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOfflineBatchRegistry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OfflineBatchRows",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SubmissionId = table.Column<long>(type: "bigint", nullable: false),
                    RowOrdinal = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RequestPayloadJson = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                    ResponsePayloadJson = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ErrorDescription = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ProcessedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OfflineBatchRows", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OfflineBatchSubmissions",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BatchNumber = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ConsumerSubject = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    OpCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RequestFileName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    RequestFileSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    RequestFileHashSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RequestFileStorageKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    RequestRowCount = table.Column<int>(type: "integer", nullable: false),
                    ResponseFileStorageKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ResponseFileHashSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ResponseFileSignatureBase64 = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    SubmittedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FailureReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    TotalRowsProcessed = table.Column<int>(type: "integer", nullable: false),
                    TotalRowsFailed = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OfflineBatchSubmissions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OfflineBatchRows_CreatedAtUtc",
                schema: "cnas",
                table: "OfflineBatchRows",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_OfflineBatchRows_IsActive",
                schema: "cnas",
                table: "OfflineBatchRows",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_OfflineBatchRows_Status",
                schema: "cnas",
                table: "OfflineBatchRows",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_OfflineBatchRows_SubmissionId_RowOrdinal",
                schema: "cnas",
                table: "OfflineBatchRows",
                columns: new[] { "SubmissionId", "RowOrdinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OfflineBatchSubmissions_BatchNumber",
                schema: "cnas",
                table: "OfflineBatchSubmissions",
                column: "BatchNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OfflineBatchSubmissions_ConsumerSubject_SubmittedAt",
                schema: "cnas",
                table: "OfflineBatchSubmissions",
                columns: new[] { "ConsumerSubject", "SubmittedAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_OfflineBatchSubmissions_CreatedAtUtc",
                schema: "cnas",
                table: "OfflineBatchSubmissions",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_OfflineBatchSubmissions_IsActive",
                schema: "cnas",
                table: "OfflineBatchSubmissions",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_OfflineBatchSubmissions_Status_OpCode",
                schema: "cnas",
                table: "OfflineBatchSubmissions",
                columns: new[] { "Status", "OpCode" });

            migrationBuilder.CreateIndex(
                name: "IX_OfflineBatchSubmissions_Status_SubmittedAt",
                schema: "cnas",
                table: "OfflineBatchSubmissions",
                columns: new[] { "Status", "SubmittedAt" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OfflineBatchRows",
                schema: "cnas");

            migrationBuilder.DropTable(
                name: "OfflineBatchSubmissions",
                schema: "cnas");
        }
    }
}
