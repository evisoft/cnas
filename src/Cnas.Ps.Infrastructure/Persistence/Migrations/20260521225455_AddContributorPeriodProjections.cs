using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddContributorPeriodProjections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ContributorPeriodProjections",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ContributorId = table.Column<long>(type: "bigint", nullable: false),
                    PeriodStartUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PeriodEndUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CivilStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    CurrentEmployerCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    MonthlySalary = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    AddressCity = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    AddressRegion = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    AddressCountry = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true),
                    PhoneE164 = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    ProjectedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContributorPeriodProjections", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ContributorPeriodProjections_Contributor_PeriodStartDesc",
                schema: "cnas",
                table: "ContributorPeriodProjections",
                columns: new[] { "ContributorId", "PeriodStartUtc" },
                unique: true,
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_ContributorPeriodProjections_CreatedAtUtc",
                schema: "cnas",
                table: "ContributorPeriodProjections",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ContributorPeriodProjections_IsActive",
                schema: "cnas",
                table: "ContributorPeriodProjections",
                column: "IsActive");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ContributorPeriodProjections",
                schema: "cnas");
        }
    }
}
