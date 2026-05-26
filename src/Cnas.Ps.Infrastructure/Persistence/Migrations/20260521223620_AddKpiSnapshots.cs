using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddKpiSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "KpiSnapshots",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SnapshotDate = table.Column<DateOnly>(type: "date", nullable: false),
                    KpiCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Value = table.Column<decimal>(type: "numeric(20,4)", nullable: false),
                    Dimension1 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, defaultValue: ""),
                    Dimension2 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, defaultValue: ""),
                    ValueUnit = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KpiSnapshots", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_KpiSnapshots_CreatedAtUtc",
                schema: "cnas",
                table: "KpiSnapshots",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_KpiSnapshots_IsActive",
                schema: "cnas",
                table: "KpiSnapshots",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_KpiSnapshots_SnapshotDateDesc_KpiCode",
                schema: "cnas",
                table: "KpiSnapshots",
                columns: new[] { "SnapshotDate", "KpiCode" },
                descending: new[] { true, false });

            migrationBuilder.CreateIndex(
                name: "UX_KpiSnapshots_NaturalKey",
                schema: "cnas",
                table: "KpiSnapshots",
                columns: new[] { "SnapshotDate", "KpiCode", "Dimension1", "Dimension2" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "KpiSnapshots",
                schema: "cnas");
        }
    }
}
