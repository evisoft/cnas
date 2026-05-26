using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// R0321 / R0224 / UI 008 — creates the <c>cnas.ApplicationVersions</c> table
    /// backing <see cref="Cnas.Ps.Core.Domain.ApplicationVersion"/>. The table is the
    /// persistence half of the autosave / draft-version-history surface: every save
    /// (autosave tick OR manual click) of an in-flight application inserts a new row
    /// that captures the form payload at that instant, allowing the citizen to revert
    /// to any prior point.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Indexes.</b> In addition to the inherited <c>(IsActive)</c> +
    /// <c>(CreatedAtUtc)</c> indexes from the
    /// <see cref="Cnas.Ps.Infrastructure.Persistence.Configurations.AuditableEntityConfiguration{TEntity}"/>
    /// base, three domain-specific indexes are declared:
    /// <list type="bullet">
    ///   <item><description><c>UNIQUE (ServiceApplicationId, VersionNumber)</c> — natural key.</description></item>
    ///   <item><description><c>UNIQUE (ServiceApplicationId, IsCurrent) WHERE IsCurrent = true</c> —
    ///     partial unique index enforcing "exactly one current row per application".</description></item>
    ///   <item><description><c>(ServiceApplicationId, CreatedAtUtc DESC)</c> — listing query support.</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>Foreign key.</b> A restrict-on-delete FK is declared to
    /// <c>cnas.ServiceApplications.Id</c> — version rows without a parent application
    /// have no meaning. Applications are soft-deleted (IsActive flag) rather than
    /// hard-deleted in normal operation so the restrict policy never triggers in
    /// practice; it is the defense-in-depth net against a future hard-delete code path.
    /// </para>
    /// </remarks>
    public partial class AddApplicationVersions : Migration
    {
        /// <summary>
        /// Creates <c>cnas.ApplicationVersions</c> with its five indexes (natural-key
        /// unique, current-row partial unique, listing composite, soft-delete,
        /// audit-timestamp) and the restrict-on-delete FK to <c>ServiceApplications</c>.
        /// </summary>
        /// <param name="migrationBuilder">EF Core migration builder.</param>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.CreateTable(
                name: "ApplicationVersions",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ServiceApplicationId = table.Column<long>(type: "bigint", nullable: false),
                    VersionNumber = table.Column<int>(type: "integer", nullable: false),
                    FormDataJson = table.Column<string>(type: "text", nullable: false),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    Source = table.Column<int>(type: "integer", nullable: false),
                    Note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    IsCurrent = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApplicationVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApplicationVersions_ServiceApplications_ServiceApplicationId",
                        column: x => x.ServiceApplicationId,
                        principalSchema: "cnas",
                        principalTable: "ServiceApplications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationVersions_CreatedAtUtc",
                schema: "cnas",
                table: "ApplicationVersions",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationVersions_IsActive",
                schema: "cnas",
                table: "ApplicationVersions",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationVersions_ServiceApplicationId_CreatedAtUtc",
                schema: "cnas",
                table: "ApplicationVersions",
                columns: new[] { "ServiceApplicationId", "CreatedAtUtc" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationVersions_ServiceApplicationId_IsCurrent",
                schema: "cnas",
                table: "ApplicationVersions",
                columns: new[] { "ServiceApplicationId", "IsCurrent" },
                unique: true,
                filter: "\"IsCurrent\" = true");

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationVersions_ServiceApplicationId_VersionNumber",
                schema: "cnas",
                table: "ApplicationVersions",
                columns: new[] { "ServiceApplicationId", "VersionNumber" },
                unique: true);
        }

        /// <summary>Drops <c>cnas.ApplicationVersions</c> and every dependent index.</summary>
        /// <param name="migrationBuilder">EF Core migration builder.</param>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropTable(
                name: "ApplicationVersions",
                schema: "cnas");
        }
    }
}
