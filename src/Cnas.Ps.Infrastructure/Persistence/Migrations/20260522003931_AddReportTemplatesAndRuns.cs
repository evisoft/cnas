using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddReportTemplatesAndRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ReportRuns",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ReportTemplateId = table.Column<long>(type: "bigint", nullable: false),
                    ExecutedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    ExecutedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RowCount = table.Column<int>(type: "integer", nullable: false),
                    OutcomeCode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DurationMs = table.Column<int>(type: "integer", nullable: false),
                    FailureReason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ReportTemplates",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Code = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Description = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    Registry = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SelectedFieldsJson = table.Column<string>(type: "text", nullable: false),
                    FilterJson = table.Column<string>(type: "text", nullable: false),
                    OrderingJson = table.Column<string>(type: "text", nullable: false),
                    GroupByField = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    OwnerUserId = table.Column<long>(type: "bigint", nullable: false),
                    IsShared = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportTemplates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReportRuns_CreatedAtUtc",
                schema: "cnas",
                table: "ReportRuns",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ReportRuns_ExecutedByUserId",
                schema: "cnas",
                table: "ReportRuns",
                column: "ExecutedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ReportRuns_IsActive",
                schema: "cnas",
                table: "ReportRuns",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_ReportRuns_ReportTemplateId_ExecutedAtUtc",
                schema: "cnas",
                table: "ReportRuns",
                columns: new[] { "ReportTemplateId", "ExecutedAtUtc" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_ReportTemplates_Code",
                schema: "cnas",
                table: "ReportTemplates",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReportTemplates_CreatedAtUtc",
                schema: "cnas",
                table: "ReportTemplates",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ReportTemplates_IsActive",
                schema: "cnas",
                table: "ReportTemplates",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_ReportTemplates_IsShared_Registry",
                schema: "cnas",
                table: "ReportTemplates",
                columns: new[] { "IsShared", "Registry" });

            migrationBuilder.CreateIndex(
                name: "IX_ReportTemplates_OwnerUserId",
                schema: "cnas",
                table: "ReportTemplates",
                column: "OwnerUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReportRuns",
                schema: "cnas");

            migrationBuilder.DropTable(
                name: "ReportTemplates",
                schema: "cnas");
        }
    }
}
