using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddExecutoryDocumentsRegistry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ExecutoryDocuments",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DocumentSeriesNumber = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DebtorIdnp = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    DebtorIdnpHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    IssuedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    IssuedDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EffectiveFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    EffectiveUntil = table.Column<DateOnly>(type: "date", nullable: true),
                    WithholdingMode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    WithholdingAmountMdl = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    WithholdingPercentage = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: true),
                    PriorityRank = table.Column<int>(type: "integer", nullable: false),
                    CreditorAccountIban = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    CreditorAccountIbanHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreditorName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    TotalOwedMdl = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    TotalWithheldMdl = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    RegisteredByUserId = table.Column<int>(type: "integer", nullable: false),
                    CompletedDate = table.Column<DateOnly>(type: "date", nullable: true),
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
                    table.PrimaryKey("PK_ExecutoryDocuments", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExecutoryDocuments_CreatedAtUtc",
                schema: "cnas",
                table: "ExecutoryDocuments",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ExecutoryDocuments_Debtor_Status_Priority",
                schema: "cnas",
                table: "ExecutoryDocuments",
                columns: new[] { "DebtorIdnpHash", "Status", "PriorityRank" });

            migrationBuilder.CreateIndex(
                name: "IX_ExecutoryDocuments_IsActive",
                schema: "cnas",
                table: "ExecutoryDocuments",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_ExecutoryDocuments_Status_EffectiveFrom",
                schema: "cnas",
                table: "ExecutoryDocuments",
                columns: new[] { "Status", "EffectiveFrom" });

            migrationBuilder.CreateIndex(
                name: "UX_ExecutoryDocuments_DocumentSeriesNumber",
                schema: "cnas",
                table: "ExecutoryDocuments",
                column: "DocumentSeriesNumber",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExecutoryDocuments",
                schema: "cnas");
        }
    }
}
