using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMassRecalculationRegistry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LegalChangeEvents",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Title = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    EffectiveFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    Scope = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    BenefitTypesInScope = table.Column<List<string>>(type: "text[]", nullable: false),
                    ChangePayloadJson = table.Column<string>(type: "character varying(16384)", maxLength: 16384, nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RegisteredByUserId = table.Column<int>(type: "integer", nullable: false),
                    CancellationReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegalChangeEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RecalculationDecisionResults",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RunId = table.Column<long>(type: "bigint", nullable: false),
                    BenefitDecisionId = table.Column<long>(type: "bigint", nullable: false),
                    BenefitType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    BeneficiaryIdnpHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OldAmountMdl = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    NewAmountMdl = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    DeltaMdl = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    RecalculationContextJson = table.Column<string>(type: "character varying(16384)", maxLength: 16384, nullable: true),
                    AppliedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecalculationDecisionResults", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RecalculationRuns",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LegalChangeEventId = table.Column<long>(type: "bigint", nullable: false),
                    TriggerKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Mode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TotalDecisionsScanned = table.Column<long>(type: "bigint", nullable: false),
                    TotalDecisionsRecalculated = table.Column<long>(type: "bigint", nullable: false),
                    TotalSkipped = table.Column<long>(type: "bigint", nullable: false),
                    TotalFailed = table.Column<long>(type: "bigint", nullable: false),
                    TotalDeltaMdl = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
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
                    table.PrimaryKey("PK_RecalculationRuns", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LegalChangeEvents_CreatedAtUtc",
                schema: "cnas",
                table: "LegalChangeEvents",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_LegalChangeEvents_IsActive",
                schema: "cnas",
                table: "LegalChangeEvents",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_LegalChangeEvents_Status_EffectiveFrom",
                schema: "cnas",
                table: "LegalChangeEvents",
                columns: new[] { "Status", "EffectiveFrom" });

            migrationBuilder.CreateIndex(
                name: "UX_LegalChangeEvents_Code",
                schema: "cnas",
                table: "LegalChangeEvents",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RecalculationDecisionResults_BeneficiaryIdnpHash",
                schema: "cnas",
                table: "RecalculationDecisionResults",
                column: "BeneficiaryIdnpHash");

            migrationBuilder.CreateIndex(
                name: "IX_RecalculationDecisionResults_CreatedAtUtc",
                schema: "cnas",
                table: "RecalculationDecisionResults",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_RecalculationDecisionResults_IsActive",
                schema: "cnas",
                table: "RecalculationDecisionResults",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_RecalculationDecisionResults_RunId",
                schema: "cnas",
                table: "RecalculationDecisionResults",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_RecalculationDecisionResults_Status_RunId",
                schema: "cnas",
                table: "RecalculationDecisionResults",
                columns: new[] { "Status", "RunId" });

            migrationBuilder.CreateIndex(
                name: "IX_RecalculationRuns_CreatedAtUtc",
                schema: "cnas",
                table: "RecalculationRuns",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_RecalculationRuns_IsActive",
                schema: "cnas",
                table: "RecalculationRuns",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_RecalculationRuns_LegalChangeEventId",
                schema: "cnas",
                table: "RecalculationRuns",
                column: "LegalChangeEventId");

            migrationBuilder.CreateIndex(
                name: "IX_RecalculationRuns_StartedAt",
                schema: "cnas",
                table: "RecalculationRuns",
                column: "StartedAt",
                descending: new bool[0]);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LegalChangeEvents",
                schema: "cnas");

            migrationBuilder.DropTable(
                name: "RecalculationDecisionResults",
                schema: "cnas");

            migrationBuilder.DropTable(
                name: "RecalculationRuns",
                schema: "cnas");
        }
    }
}
