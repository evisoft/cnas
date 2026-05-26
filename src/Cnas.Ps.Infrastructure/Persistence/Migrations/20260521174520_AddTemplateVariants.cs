using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// R0133 / R0134 / TOR CF 17.16 / CF 17.17 — Introduces per-language variants for
    /// <c>DocumentTemplates</c>. Adds:
    /// <list type="bullet">
    ///   <item><c>DocumentTemplates.DefaultLanguage</c> — non-null fallback locale (default <c>'ro'</c>).</item>
    ///   <item><c>cnas.TemplateVariants</c> table with the unique <c>(TemplateId, Language)</c> index.</item>
    ///   <item>Back-fill DO block that materialises one <c>'ro'</c>-language variant per
    ///         existing <c>DocumentTemplates</c> row so the renderer's per-variant
    ///         lookup yields a hit immediately after the migration applies.</item>
    /// </list>
    /// </summary>
    public partial class AddTemplateVariants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.AddColumn<string>(
                name: "DefaultLanguage",
                schema: "cnas",
                table: "DocumentTemplates",
                type: "character varying(8)",
                maxLength: 8,
                nullable: false,
                defaultValue: "ro");

            migrationBuilder.CreateTable(
                name: "TemplateVariants",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TemplateId = table.Column<long>(type: "bigint", nullable: false),
                    Language = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    SubjectOrTitle = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Body = table.Column<string>(type: "text", nullable: false),
                    RenderedDocxBytes = table.Column<byte[]>(type: "bytea", nullable: true),
                    DocxFileName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    IsApproved = table.Column<bool>(type: "boolean", nullable: false),
                    TranslatorNote = table.Column<string>(type: "text", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TemplateVariants", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TemplateVariants_CreatedAtUtc",
                schema: "cnas",
                table: "TemplateVariants",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_TemplateVariants_IsActive",
                schema: "cnas",
                table: "TemplateVariants",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_TemplateVariants_TemplateId_Language",
                schema: "cnas",
                table: "TemplateVariants",
                columns: new[] { "TemplateId", "Language" },
                unique: true);

            // R0133 back-fill — for every existing DocumentTemplate row insert a
            // baseline RO variant so the renderer's per-variant lookup keeps working
            // without code changes. SubjectOrTitle and Body are derived from the
            // template's Name and Description: Name is always populated; Description
            // is nullable so we fall back to a single-space placeholder body to
            // satisfy the NOT NULL constraint. Operators may overwrite these
            // back-filled rows immediately via the variant upsert endpoint.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    INSERT INTO cnas."TemplateVariants" (
                        "TemplateId", "Language", "SubjectOrTitle", "Body",
                        "IsApproved", "CreatedAtUtc", "IsActive")
                    SELECT
                        t."Id",
                        'ro',
                        t."Name",
                        COALESCE(NULLIF(t."Description", ''), ' '),
                        true,
                        NOW() AT TIME ZONE 'UTC',
                        true
                    FROM cnas."DocumentTemplates" t
                    WHERE t."IsActive" = true
                    ON CONFLICT ("TemplateId", "Language") DO NOTHING;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropTable(
                name: "TemplateVariants",
                schema: "cnas");

            migrationBuilder.DropColumn(
                name: "DefaultLanguage",
                schema: "cnas",
                table: "DocumentTemplates");
        }
    }
}
