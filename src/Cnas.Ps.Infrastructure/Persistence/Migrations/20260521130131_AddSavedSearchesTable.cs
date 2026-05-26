using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// R0165 / CF 03.06 — creates the <c>cnas.SavedSearches</c> table backing
    /// <see cref="Cnas.Ps.Core.Domain.SavedSearch"/>. The table is the persistence half
    /// of the saved-search surface; binaries (filter JSON) live in-row as <c>text</c>
    /// because the payloads are small (capped at 8192 bytes by the service layer).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Access model.</b> The ownership / sharing rules are enforced at the service
    /// layer (see <c>SavedSearchService</c>); the schema simply stores <c>OwnerUserId</c>
    /// and the <c>IsShared</c> flag. No FK is declared against <c>UserProfiles</c> — we
    /// mirror the same pattern used elsewhere (e.g. <c>WorkflowTask.AssignedUserId</c>)
    /// because the application enforces the foreign-key semantics and a hard FK would
    /// fight GDPR right-to-erasure cascades on the user side.
    /// </para>
    /// <para>
    /// <b>Indexes.</b> Four indexes are declared in addition to the soft-delete +
    /// audit-timestamp indexes contributed by the
    /// <see cref="Cnas.Ps.Infrastructure.Persistence.Configurations.AuditableEntityConfiguration{TEntity}"/>
    /// base:
    /// <list type="bullet">
    ///   <item>
    ///     <description>
    ///       <c>UNIQUE (OwnerUserId, Registry, Name)</c> — natural key. The service treats
    ///       a duplicate triple as an idempotent return of the existing Sqid; the unique
    ///       constraint is the DB-side safety net against a racing concurrent insert.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///       <c>(IsShared, Registry)</c> — supports the shared-rows half of the list
    ///       query when a non-owner reads published rows for a specific registry.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///       <c>(OwnerUserId)</c> — supports the own-rows half of the list query and
    ///       the per-owner-cap count.
    ///     </description>
    ///   </item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>Down migration.</b> Drops the table cleanly — there are no foreign keys into
    /// or out of <c>SavedSearches</c>, so a rollback is a single <c>DROP TABLE</c>.
    /// </para>
    /// </remarks>
    public partial class AddSavedSearchesTable : Migration
    {
        /// <summary>
        /// Creates <c>cnas.SavedSearches</c> with its five indexes (natural-key unique,
        /// shared-list, owner-list, soft-delete, audit-timestamp).
        /// </summary>
        /// <param name="migrationBuilder">EF Core migration builder.</param>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.CreateTable(
                name: "SavedSearches",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OwnerUserId = table.Column<long>(type: "bigint", nullable: false),
                    Registry = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    FilterJson = table.Column<string>(type: "text", nullable: false),
                    IsShared = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SavedSearches", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SavedSearches_CreatedAtUtc",
                schema: "cnas",
                table: "SavedSearches",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_SavedSearches_IsActive",
                schema: "cnas",
                table: "SavedSearches",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_SavedSearches_IsShared_Registry",
                schema: "cnas",
                table: "SavedSearches",
                columns: new[] { "IsShared", "Registry" });

            migrationBuilder.CreateIndex(
                name: "IX_SavedSearches_OwnerUserId",
                schema: "cnas",
                table: "SavedSearches",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_SavedSearches_OwnerUserId_Registry_Name",
                schema: "cnas",
                table: "SavedSearches",
                columns: new[] { "OwnerUserId", "Registry", "Name" },
                unique: true);
        }

        /// <summary>Drops <c>cnas.SavedSearches</c> and every dependent index.</summary>
        /// <param name="migrationBuilder">EF Core migration builder.</param>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropTable(
                name: "SavedSearches",
                schema: "cnas");
        }
    }
}
