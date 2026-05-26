using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentVerdictFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Verdict",
                schema: "cnas",
                table: "Documents",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VerdictNote",
                schema: "cnas",
                table: "Documents",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<System.DateTime>(
                name: "VerdictAtUtc",
                schema: "cnas",
                table: "Documents",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Verdict",
                schema: "cnas",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "VerdictNote",
                schema: "cnas",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "VerdictAtUtc",
                schema: "cnas",
                table: "Documents");
        }
    }
}
