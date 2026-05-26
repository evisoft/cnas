using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// R0535 / CF 04.07-08 + R0227 / TOR UI 014 — combined head migration that
    /// (a) adds the <c>UserProfiles.LayoutPreferences</c> JSONB column carrying each
    /// user's UI layout preferences (grid column visibility / order, page-size
    /// defaults, dashboard widget order) AND (b) re-creates the
    /// <c>cnas.AttachmentRecords</c> table that backs the reusable attachment widget.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why combined.</b> The two changes ship together because the previous
    /// AttachmentRecords migration was rolled back by an accidental design-time
    /// <c>dotnet ef migrations remove</c> against an unreachable database. Recreating
    /// the AttachmentRecords schema here keeps the migration chain monotonic and
    /// guarantees that a fresh <c>dotnet ef database update</c> from any prior point
    /// in history produces the same final schema.
    /// </para>
    /// <para>
    /// <b>Nullable column, no back-fill (R0535).</b> <c>LayoutPreferences</c> is added
    /// nullable so existing rows materialise with <c>NULL</c>. The application
    /// service parses NULL as <see cref="Cnas.Ps.Core.ValueObjects.UserLayoutPreferences.Default"/>
    /// (every grid uses registry defaults, system page size, empty widget order), so
    /// pre-migration users continue to see the same UI until they explicitly save a
    /// layout via <c>PUT /api/profile/layout-preferences</c>. No DML is required at
    /// migration time.
    /// </para>
    /// <para>
    /// <b>JSONB rationale.</b> The column stores only column / widget identifier
    /// strings and integer page-size values; JSONB gives compact storage and future
    /// indexability without paying the per-document parse cost of <c>jsonpath</c>
    /// queries on plain TEXT. The InMemory provider used in tests treats it as a
    /// regular nullable string column — see <c>UserProfileConfiguration</c> for the
    /// EF wiring.
    /// </para>
    /// <para>
    /// <b>AttachmentRecords schema (R0227).</b> Standard auditable-entity convention
    /// (xmin concurrency token, soft-delete via <c>IsActive</c>). The filtered unique
    /// constraint on <c>(OwnerEntityType, OwnerEntityId, Sha256Hex) WHERE IsActive=true</c>
    /// enforces the per-owner dedup contract documented on the entity. Secondary
    /// indexes on <c>(OwnerEntityType, OwnerEntityId)</c> and the auditable defaults
    /// (<c>CreatedAtUtc</c>, <c>IsActive</c>) support the per-owner listing query.
    /// </para>
    /// <para>
    /// <b>No PII drift.</b> Neither the JSON column nor the AttachmentRecords table
    /// participates in the encrypted-at-rest set (CLAUDE.md §5.7 / TOR SEC 035).
    /// </para>
    /// </remarks>
    public partial class AddUserProfileLayoutPreferencesAndAttachmentRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.AddColumn<string>(
                name: "LayoutPreferences",
                schema: "cnas",
                table: "UserProfiles",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AttachmentRecords",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OwnerEntityType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OwnerEntityId = table.Column<long>(type: "bigint", nullable: false),
                    FileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    StorageKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Sha256Hex = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Category = table.Column<int>(type: "integer", nullable: false),
                    SensitivityLevel = table.Column<int>(type: "integer", nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    UploadedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    UploadedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsArchived = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AttachmentRecords", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AttachmentRecords_CreatedAtUtc",
                schema: "cnas",
                table: "AttachmentRecords",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AttachmentRecords_IsActive",
                schema: "cnas",
                table: "AttachmentRecords",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_AttachmentRecords_OwnerEntityType_OwnerEntityId",
                schema: "cnas",
                table: "AttachmentRecords",
                columns: new[] { "OwnerEntityType", "OwnerEntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_AttachmentRecords_OwnerEntityType_OwnerEntityId_Sha256Hex",
                schema: "cnas",
                table: "AttachmentRecords",
                columns: new[] { "OwnerEntityType", "OwnerEntityId", "Sha256Hex" },
                unique: true,
                filter: "\"IsActive\" = true");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropTable(
                name: "AttachmentRecords",
                schema: "cnas");

            migrationBuilder.DropColumn(
                name: "LayoutPreferences",
                schema: "cnas",
                table: "UserProfiles");
        }
    }
}
