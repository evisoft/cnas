using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAthletePensionRegistry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AthleteCareerRecords",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AwardId = table.Column<long>(type: "bigint", nullable: false),
                    AchievementKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AchievementYear = table.Column<int>(type: "integer", nullable: false),
                    Event = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Years = table.Column<int>(type: "integer", nullable: true),
                    Verified = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    VerifiedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    VerifiedByUserId = table.Column<int>(type: "integer", nullable: true),
                    VerificationNote = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    EvidenceDocumentReference = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AthleteCareerRecords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AthletePensionAwards",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AwardNumber = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    BeneficiaryIdnp = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    BeneficiaryIdnpHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    BeneficiaryDisplayName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    BeneficiaryBirthDate = table.Column<DateOnly>(type: "date", nullable: false),
                    BeneficiarySex = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    SportDiscipline = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RequestedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ApprovedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RejectedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RejectionReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    EffectiveFrom = table.Column<DateOnly>(type: "date", nullable: true),
                    SuspendedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SuspensionReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    TerminatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TerminationReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    MonthlyAmountMdl = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    RegulatoryBaseMdl = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    MultiplierPercent = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    EligibilityNotesJson = table.Column<string>(type: "character varying(32768)", maxLength: 32768, nullable: true),
                    RegisteredByUserId = table.Column<int>(type: "integer", nullable: false),
                    LastRecomputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AthletePensionAwards", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AthleteCareerRecords_AwardId",
                schema: "cnas",
                table: "AthleteCareerRecords",
                column: "AwardId");

            migrationBuilder.CreateIndex(
                name: "IX_AthleteCareerRecords_CreatedAtUtc",
                schema: "cnas",
                table: "AthleteCareerRecords",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AthleteCareerRecords_IsActive",
                schema: "cnas",
                table: "AthleteCareerRecords",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_AthleteCareerRecords_Kind_Year",
                schema: "cnas",
                table: "AthleteCareerRecords",
                columns: new[] { "AchievementKind", "AchievementYear" });

            migrationBuilder.CreateIndex(
                name: "IX_AthletePensionAwards_BeneficiaryIdnpHash",
                schema: "cnas",
                table: "AthletePensionAwards",
                column: "BeneficiaryIdnpHash");

            migrationBuilder.CreateIndex(
                name: "IX_AthletePensionAwards_CreatedAtUtc",
                schema: "cnas",
                table: "AthletePensionAwards",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AthletePensionAwards_IsActive",
                schema: "cnas",
                table: "AthletePensionAwards",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_AthletePensionAwards_Status_EffectiveFrom",
                schema: "cnas",
                table: "AthletePensionAwards",
                columns: new[] { "Status", "EffectiveFrom" });

            migrationBuilder.CreateIndex(
                name: "UX_AthletePensionAwards_AwardNumber",
                schema: "cnas",
                table: "AthletePensionAwards",
                column: "AwardNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_AthletePensionAwards_Beneficiary_Role_Active",
                schema: "cnas",
                table: "AthletePensionAwards",
                columns: new[] { "BeneficiaryIdnpHash", "Role" },
                unique: true,
                filter: "\"Status\" NOT IN ('Rejected', 'Terminated')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AthleteCareerRecords",
                schema: "cnas");

            migrationBuilder.DropTable(
                name: "AthletePensionAwards",
                schema: "cnas");
        }
    }
}
