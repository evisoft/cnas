using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRev5DeclarationsAndAdjustments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InsuredPersonContributionAdjustments",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    InsuredPersonSolicitantId = table.Column<long>(type: "bigint", nullable: false),
                    Month = table.Column<DateOnly>(type: "date", nullable: false),
                    AdjustmentAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    SourceDocumentCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SourceDocumentReference = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InsuredPersonContributionAdjustments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Rev5Declarations",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FilingContributorId = table.Column<long>(type: "bigint", nullable: false),
                    ReportingMonth = table.Column<DateOnly>(type: "date", nullable: false),
                    FiledAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReferenceNumber = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    TotalDeclaredAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    RowCount = table.Column<int>(type: "integer", nullable: false),
                    Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Rev5Declarations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Rev5DeclarationRows",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Rev5DeclarationId = table.Column<long>(type: "bigint", nullable: false),
                    InsuredPersonNationalIdHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ContributionBaseAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    ContributionAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    DaysWorked = table.Column<int>(type: "integer", nullable: true),
                    PositionCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Rev5DeclarationRows", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Rev5DeclarationRows_Rev5Declarations_Rev5DeclarationId",
                        column: x => x.Rev5DeclarationId,
                        principalSchema: "cnas",
                        principalTable: "Rev5Declarations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InsuredPersonContributionAdjustments_CreatedAtUtc",
                schema: "cnas",
                table: "InsuredPersonContributionAdjustments",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_InsuredPersonContributionAdjustments_IsActive",
                schema: "cnas",
                table: "InsuredPersonContributionAdjustments",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_InsuredPersonContributionAdjustments_Solicitant_Month",
                schema: "cnas",
                table: "InsuredPersonContributionAdjustments",
                columns: new[] { "InsuredPersonSolicitantId", "Month" });

            migrationBuilder.CreateIndex(
                name: "IX_Rev5DeclarationRows_CreatedAtUtc",
                schema: "cnas",
                table: "Rev5DeclarationRows",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Rev5DeclarationRows_IsActive",
                schema: "cnas",
                table: "Rev5DeclarationRows",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_Rev5DeclarationRows_NationalIdHash",
                schema: "cnas",
                table: "Rev5DeclarationRows",
                column: "InsuredPersonNationalIdHash");

            migrationBuilder.CreateIndex(
                name: "UX_Rev5DeclarationRows_NaturalKey",
                schema: "cnas",
                table: "Rev5DeclarationRows",
                columns: new[] { "Rev5DeclarationId", "InsuredPersonNationalIdHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Rev5Declarations_Contributor_Month",
                schema: "cnas",
                table: "Rev5Declarations",
                columns: new[] { "FilingContributorId", "ReportingMonth" });

            migrationBuilder.CreateIndex(
                name: "IX_Rev5Declarations_CreatedAtUtc",
                schema: "cnas",
                table: "Rev5Declarations",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Rev5Declarations_IsActive",
                schema: "cnas",
                table: "Rev5Declarations",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "UX_Rev5Declarations_NaturalKey",
                schema: "cnas",
                table: "Rev5Declarations",
                columns: new[] { "FilingContributorId", "ReportingMonth", "ReferenceNumber" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InsuredPersonContributionAdjustments",
                schema: "cnas");

            migrationBuilder.DropTable(
                name: "Rev5DeclarationRows",
                schema: "cnas");

            migrationBuilder.DropTable(
                name: "Rev5Declarations",
                schema: "cnas");
        }
    }
}
