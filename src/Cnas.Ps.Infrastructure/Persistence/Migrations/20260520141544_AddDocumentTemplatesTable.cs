using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Creates the <c>cnas.DocumentTemplates</c> table that backs
    /// <see cref="Cnas.Ps.Core.Domain.DocumentTemplate"/>. UC17 phase 2A persists
    /// operator-uploaded DOCX templates here; the binaries themselves live in MinIO
    /// under the <c>cnas-templates</c> bucket. Mirrors the versioning shape of the
    /// adjacent <c>WorkflowDefinitions</c> table.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Versioning model.</b> Each upload of a code inserts a new row with
    /// <c>Version</c> = previous + 1 and <c>IsCurrent = true</c>; the previous current
    /// row is updated in the same transaction to <c>IsCurrent = false</c>. Old rows are
    /// never deleted, so a complete audit history is queryable. The catalog endpoint
    /// only surfaces rows whose <c>IsCurrent = true AND IsActive = true</c>.
    /// </para>
    /// <para>
    /// Two domain-specific indexes are created:
    /// <list type="bullet">
    ///   <item><description><c>UNIQUE (Code, Version)</c> — natural key; protects against double-publish bugs.</description></item>
    ///   <item><description><c>(Code, IsCurrent) WHERE IsCurrent = true</c> — partial index serving the GetByCode lookup.</description></item>
    /// </list>
    /// The standard <c>(IsActive)</c> and <c>(CreatedAtUtc)</c> indexes inherited from
    /// <see cref="Cnas.Ps.Infrastructure.Persistence.Configurations.AuditableEntityConfiguration{TEntity}"/>
    /// are also created.
    /// </para>
    /// <para>
    /// Down migration drops the table cleanly — there are no foreign keys into or out
    /// of this row because the document templates are referenced by code (a string),
    /// not by surrogate key.
    /// </para>
    /// </remarks>
    public partial class AddDocumentTemplatesTable : Migration
    {
        /// <summary>
        /// Creates <c>cnas.DocumentTemplates</c> with its four indexes (natural-key
        /// unique, partial-current lookup, soft-delete, audit-timestamp).
        /// </summary>
        /// <param name="migrationBuilder">EF Core migration builder.</param>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DocumentTemplates",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Code = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    IsCurrent = table.Column<bool>(type: "boolean", nullable: false),
                    StorageObjectKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ContentLength = table.Column<long>(type: "bigint", nullable: false),
                    ContentSha256 = table.Column<string>(type: "char(64)", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentTemplates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentTemplates_Code_IsCurrent",
                schema: "cnas",
                table: "DocumentTemplates",
                columns: new[] { "Code", "IsCurrent" },
                filter: "\"IsCurrent\" = true");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentTemplates_Code_Version",
                schema: "cnas",
                table: "DocumentTemplates",
                columns: new[] { "Code", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DocumentTemplates_CreatedAtUtc",
                schema: "cnas",
                table: "DocumentTemplates",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentTemplates_IsActive",
                schema: "cnas",
                table: "DocumentTemplates",
                column: "IsActive");
        }

        /// <summary>Drops <c>cnas.DocumentTemplates</c> and all its indexes.</summary>
        /// <param name="migrationBuilder">EF Core migration builder.</param>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DocumentTemplates",
                schema: "cnas");
        }
    }
}
