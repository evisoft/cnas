using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLaborBookletsAndPre1999Periods : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InsuredPersonPre1999Periods",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    InsuredPersonSolicitantId = table.Column<long>(type: "bigint", nullable: false),
                    LaborBookletId = table.Column<long>(type: "bigint", nullable: true),
                    PeriodStartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    PeriodEndDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EmployerName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Position = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    DaysWorked = table.Column<int>(type: "integer", nullable: true),
                    ProofDocumentReference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ValidFromUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ValidToUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ChangeReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    RecordedByUserSqid = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InsuredPersonPre1999Periods", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LaborBooklets",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    InsuredPersonSolicitantId = table.Column<long>(type: "bigint", nullable: false),
                    CarnetMuncaNumber = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    IssuedDate = table.Column<DateOnly>(type: "date", nullable: true),
                    IssuingAuthority = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    OcrExtractedJson = table.Column<string>(type: "text", nullable: true),
                    OcrConfidenceLevel = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    VerifierNotes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    VerifiedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    VerifiedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RejectionReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    RejectedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    HasScannedCopy = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LaborBooklets", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InsuredPersonPre1999Periods_CreatedAtUtc",
                schema: "cnas",
                table: "InsuredPersonPre1999Periods",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_InsuredPersonPre1999Periods_IsActive",
                schema: "cnas",
                table: "InsuredPersonPre1999Periods",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_InsuredPersonPre1999Periods_LaborBookletId",
                schema: "cnas",
                table: "InsuredPersonPre1999Periods",
                column: "LaborBookletId");

            migrationBuilder.CreateIndex(
                name: "IX_InsuredPersonPre1999Periods_Solicitant_StartDate",
                schema: "cnas",
                table: "InsuredPersonPre1999Periods",
                columns: new[] { "InsuredPersonSolicitantId", "PeriodStartDate" });

            migrationBuilder.CreateIndex(
                name: "IX_LaborBooklets_CreatedAtUtc",
                schema: "cnas",
                table: "LaborBooklets",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_LaborBooklets_IsActive",
                schema: "cnas",
                table: "LaborBooklets",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_LaborBooklets_Solicitant_Status",
                schema: "cnas",
                table: "LaborBooklets",
                columns: new[] { "InsuredPersonSolicitantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "UX_LaborBooklets_PerSolicitant_Number",
                schema: "cnas",
                table: "LaborBooklets",
                columns: new[] { "InsuredPersonSolicitantId", "CarnetMuncaNumber" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InsuredPersonPre1999Periods",
                schema: "cnas");

            migrationBuilder.DropTable(
                name: "LaborBooklets",
                schema: "cnas");
        }
    }
}
