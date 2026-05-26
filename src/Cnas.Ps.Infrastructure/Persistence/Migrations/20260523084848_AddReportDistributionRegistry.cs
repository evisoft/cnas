using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddReportDistributionRegistry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ReportDistributionRules",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ReportCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Channel = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RecipientKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RecipientCode = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    RecipientCodeHash = table.Column<string>(type: "character varying(44)", maxLength: 44, nullable: true),
                    Format = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Priority = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    EffectiveFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    EffectiveUntil = table.Column<DateOnly>(type: "date", nullable: true),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportDistributionRules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ReportDistributionDispatches",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RuleId = table.Column<long>(type: "bigint", nullable: false),
                    ReportRunSqid = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Channel = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RecipientKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RecipientCode = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DispatchedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeliveredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FailureReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    RetryCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportDistributionDispatches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReportDistributionDispatches_ReportDistributionRules_RuleId",
                        column: x => x.RuleId,
                        principalSchema: "cnas",
                        principalTable: "ReportDistributionRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReportDistributionDispatches_CreatedAtUtc",
                schema: "cnas",
                table: "ReportDistributionDispatches",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ReportDistributionDispatches_IsActive",
                schema: "cnas",
                table: "ReportDistributionDispatches",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_ReportDistributionDispatches_ReportRunSqid",
                schema: "cnas",
                table: "ReportDistributionDispatches",
                column: "ReportRunSqid");

            migrationBuilder.CreateIndex(
                name: "IX_ReportDistributionDispatches_RuleId",
                schema: "cnas",
                table: "ReportDistributionDispatches",
                column: "RuleId");

            migrationBuilder.CreateIndex(
                name: "IX_ReportDistributionDispatches_Status_DispatchedAt",
                schema: "cnas",
                table: "ReportDistributionDispatches",
                columns: new[] { "Status", "DispatchedAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_ReportDistributionRules_CreatedAtUtc",
                schema: "cnas",
                table: "ReportDistributionRules",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ReportDistributionRules_IsActive",
                schema: "cnas",
                table: "ReportDistributionRules",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_ReportDistributionRules_ReportCode",
                schema: "cnas",
                table: "ReportDistributionRules",
                column: "ReportCode");

            migrationBuilder.CreateIndex(
                name: "IX_ReportDistributionRules_ReportCode_Channel_IsActive",
                schema: "cnas",
                table: "ReportDistributionRules",
                columns: new[] { "ReportCode", "Channel", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "UX_ReportDistributionRules_NaturalKey",
                schema: "cnas",
                table: "ReportDistributionRules",
                columns: new[] { "ReportCode", "Channel", "RecipientKind", "RecipientCodeHash", "RecipientCode" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReportDistributionDispatches",
                schema: "cnas");

            migrationBuilder.DropTable(
                name: "ReportDistributionRules",
                schema: "cnas");
        }
    }
}
