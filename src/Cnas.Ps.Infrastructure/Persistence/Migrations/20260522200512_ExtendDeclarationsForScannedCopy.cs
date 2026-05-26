using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ExtendDeclarationsForScannedCopy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FormVersion",
                schema: "cnas",
                table: "Declarations",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "HasScannedCopy",
                schema: "cnas",
                table: "Declarations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "OcrConfidenceLevel",
                schema: "cnas",
                table: "Declarations",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OcrExtractedJson",
                schema: "cnas",
                table: "Declarations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RegisteredByOffice",
                schema: "cnas",
                table: "Declarations",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Declarations_HasScannedCopy",
                schema: "cnas",
                table: "Declarations",
                column: "HasScannedCopy");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Declarations_HasScannedCopy",
                schema: "cnas",
                table: "Declarations");

            migrationBuilder.DropColumn(
                name: "FormVersion",
                schema: "cnas",
                table: "Declarations");

            migrationBuilder.DropColumn(
                name: "HasScannedCopy",
                schema: "cnas",
                table: "Declarations");

            migrationBuilder.DropColumn(
                name: "OcrConfidenceLevel",
                schema: "cnas",
                table: "Declarations");

            migrationBuilder.DropColumn(
                name: "OcrExtractedJson",
                schema: "cnas",
                table: "Declarations");

            migrationBuilder.DropColumn(
                name: "RegisteredByOffice",
                schema: "cnas",
                table: "Declarations");
        }
    }
}
