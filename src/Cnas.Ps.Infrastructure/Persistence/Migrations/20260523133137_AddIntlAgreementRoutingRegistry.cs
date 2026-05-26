using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddIntlAgreementRoutingRegistry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IntlAgreementReviewCases",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CaseNumber = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    BenefitKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    BeneficiaryIdnp = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    BeneficiaryIdnpHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    BeneficiaryDisplayName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    AgreementCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    HostCountryCode = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CurrentLevel = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ReferenceBenefitPassportSqid = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    SubmittedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ApprovedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RejectedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RejectionReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    RevisionRequestedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RevisionRequestNote = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CancelledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CancelReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    RegisteredByUserId = table.Column<int>(type: "integer", nullable: false),
                    EvidenceJson = table.Column<string>(type: "character varying(16384)", maxLength: 16384, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntlAgreementReviewCases", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IntlAgreementReviewSteps",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CaseId = table.Column<long>(type: "bigint", nullable: false),
                    Level = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ReviewedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReviewedByUserId = table.Column<int>(type: "integer", nullable: false),
                    Note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntlAgreementReviewSteps", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IntlAgreementReviewCases_BeneficiaryIdnpHash",
                schema: "cnas",
                table: "IntlAgreementReviewCases",
                column: "BeneficiaryIdnpHash");

            migrationBuilder.CreateIndex(
                name: "IX_IntlAgreementReviewCases_BenefitKind_Status",
                schema: "cnas",
                table: "IntlAgreementReviewCases",
                columns: new[] { "BenefitKind", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_IntlAgreementReviewCases_CreatedAtUtc",
                schema: "cnas",
                table: "IntlAgreementReviewCases",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_IntlAgreementReviewCases_IsActive",
                schema: "cnas",
                table: "IntlAgreementReviewCases",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_IntlAgreementReviewCases_Status_CurrentLevel",
                schema: "cnas",
                table: "IntlAgreementReviewCases",
                columns: new[] { "Status", "CurrentLevel" });

            migrationBuilder.CreateIndex(
                name: "UX_IntlAgreementReviewCases_Beneficiary_Agreement_Active",
                schema: "cnas",
                table: "IntlAgreementReviewCases",
                columns: new[] { "BeneficiaryIdnpHash", "AgreementCode", "BenefitKind" },
                unique: true,
                filter: "\"Status\" NOT IN ('Approved', 'Rejected', 'Cancelled')");

            migrationBuilder.CreateIndex(
                name: "UX_IntlAgreementReviewCases_CaseNumber",
                schema: "cnas",
                table: "IntlAgreementReviewCases",
                column: "CaseNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IntlAgreementReviewSteps_CaseId",
                schema: "cnas",
                table: "IntlAgreementReviewSteps",
                column: "CaseId");

            migrationBuilder.CreateIndex(
                name: "IX_IntlAgreementReviewSteps_CaseId_ReviewedAt",
                schema: "cnas",
                table: "IntlAgreementReviewSteps",
                columns: new[] { "CaseId", "ReviewedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_IntlAgreementReviewSteps_CreatedAtUtc",
                schema: "cnas",
                table: "IntlAgreementReviewSteps",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_IntlAgreementReviewSteps_IsActive",
                schema: "cnas",
                table: "IntlAgreementReviewSteps",
                column: "IsActive");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IntlAgreementReviewCases",
                schema: "cnas");

            migrationBuilder.DropTable(
                name: "IntlAgreementReviewSteps",
                schema: "cnas");
        }
    }
}
