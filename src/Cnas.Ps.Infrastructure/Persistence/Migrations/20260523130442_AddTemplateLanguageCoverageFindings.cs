using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTemplateLanguageCoverageFindings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TemplateLanguageCoverageFindings",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TemplateId = table.Column<long>(type: "bigint", nullable: false),
                    MissingLanguage = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    DetectedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Acknowledged = table.Column<bool>(type: "boolean", nullable: false),
                    AcknowledgedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AcknowledgedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    AcknowledgementNote = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TemplateLanguageCoverageFindings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TemplateLanguageCoverageFindings_DocumentTemplates_Template~",
                        column: x => x.TemplateId,
                        principalSchema: "cnas",
                        principalTable: "DocumentTemplates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TemplateLanguageCoverageFindings_CreatedAtUtc",
                schema: "cnas",
                table: "TemplateLanguageCoverageFindings",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_TemplateLanguageCoverageFindings_DetectedAt",
                schema: "cnas",
                table: "TemplateLanguageCoverageFindings",
                column: "DetectedAt",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_TemplateLanguageCoverageFindings_IsActive",
                schema: "cnas",
                table: "TemplateLanguageCoverageFindings",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_TemplateLanguageCoverageFindings_Language_Acknowledged",
                schema: "cnas",
                table: "TemplateLanguageCoverageFindings",
                columns: new[] { "MissingLanguage", "Acknowledged" });

            migrationBuilder.CreateIndex(
                name: "UX_TemplateLanguageCoverageFindings_Open",
                schema: "cnas",
                table: "TemplateLanguageCoverageFindings",
                columns: new[] { "TemplateId", "MissingLanguage", "Acknowledged" },
                unique: true,
                filter: "\"Acknowledged\" = false");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TemplateLanguageCoverageFindings",
                schema: "cnas");
        }
    }
}
