using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProfileUpdateAndRefreshRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProfileRefreshRuns",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    TargetContributorId = table.Column<long>(type: "bigint", nullable: true),
                    Outcome = table.Column<int>(type: "integer", nullable: false),
                    RowsApplied = table.Column<int>(type: "integer", nullable: false),
                    RowsSkipped = table.Column<int>(type: "integer", nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FailureSummary = table.Column<string>(type: "character varying(5000)", maxLength: 5000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProfileRefreshRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProfileUpdateRequests",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ServiceApplicationId = table.Column<long>(type: "bigint", nullable: false),
                    TargetContributorId = table.Column<long>(type: "bigint", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    RequestedChangesJson = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    RejectionReason = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    AppliedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ApprovedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    ApplicationErrorJson = table.Column<string>(type: "text", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProfileUpdateRequests", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProfileRefreshRuns_CreatedAtUtc",
                schema: "cnas",
                table: "ProfileRefreshRuns",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ProfileRefreshRuns_IsActive",
                schema: "cnas",
                table: "ProfileRefreshRuns",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_ProfileRefreshRuns_Source_StartedUtcDesc",
                schema: "cnas",
                table: "ProfileRefreshRuns",
                columns: new[] { "Source", "StartedUtc" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_ProfileRefreshRuns_TargetContributorId",
                schema: "cnas",
                table: "ProfileRefreshRuns",
                column: "TargetContributorId");

            migrationBuilder.CreateIndex(
                name: "IX_ProfileUpdateRequests_Contributor_Status",
                schema: "cnas",
                table: "ProfileUpdateRequests",
                columns: new[] { "TargetContributorId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ProfileUpdateRequests_CreatedAtUtc",
                schema: "cnas",
                table: "ProfileUpdateRequests",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ProfileUpdateRequests_IsActive",
                schema: "cnas",
                table: "ProfileUpdateRequests",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "UX_ProfileUpdateRequests_ServiceApplicationId",
                schema: "cnas",
                table: "ProfileUpdateRequests",
                column: "ServiceApplicationId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProfileRefreshRuns",
                schema: "cnas");

            migrationBuilder.DropTable(
                name: "ProfileUpdateRequests",
                schema: "cnas");
        }
    }
}
