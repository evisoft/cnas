using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTreasuryFeedRegistry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TreasuryFeedImportRows",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ImportId = table.Column<long>(type: "bigint", nullable: false),
                    RowOrdinal = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RawPayloadJson = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                    MappedReceiptId = table.Column<long>(type: "bigint", nullable: true),
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
                    table.PrimaryKey("PK_TreasuryFeedImportRows", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TreasuryFeedImports",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FeedDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SourceKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SourceReference = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    FileSizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    FileHashSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    RowsTotal = table.Column<int>(type: "integer", nullable: false),
                    RowsImported = table.Column<int>(type: "integer", nullable: false),
                    RowsUpdated = table.Column<int>(type: "integer", nullable: false),
                    RowsSkipped = table.Column<int>(type: "integer", nullable: false),
                    RowsFailed = table.Column<int>(type: "integer", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FailureReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    TriggerKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TreasuryFeedImports", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TreasuryFeedImportRows_CreatedAtUtc",
                schema: "cnas",
                table: "TreasuryFeedImportRows",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_TreasuryFeedImportRows_ImportId_RowOrdinal",
                schema: "cnas",
                table: "TreasuryFeedImportRows",
                columns: new[] { "ImportId", "RowOrdinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TreasuryFeedImportRows_IsActive",
                schema: "cnas",
                table: "TreasuryFeedImportRows",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_TreasuryFeedImportRows_Status_ImportId",
                schema: "cnas",
                table: "TreasuryFeedImportRows",
                columns: new[] { "Status", "ImportId" });

            migrationBuilder.CreateIndex(
                name: "IX_TreasuryFeedImports_CreatedAtUtc",
                schema: "cnas",
                table: "TreasuryFeedImports",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_TreasuryFeedImports_FeedDate_SourceKind_Completed",
                schema: "cnas",
                table: "TreasuryFeedImports",
                columns: new[] { "FeedDate", "SourceKind" },
                unique: true,
                filter: "\"Status\" = 'Completed'");

            migrationBuilder.CreateIndex(
                name: "IX_TreasuryFeedImports_IsActive",
                schema: "cnas",
                table: "TreasuryFeedImports",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_TreasuryFeedImports_Status_StartedAt",
                schema: "cnas",
                table: "TreasuryFeedImports",
                columns: new[] { "Status", "StartedAt" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TreasuryFeedImportRows",
                schema: "cnas");

            migrationBuilder.DropTable(
                name: "TreasuryFeedImports",
                schema: "cnas");
        }
    }
}
