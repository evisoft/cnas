using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// R0166 / TOR CF 03.11 / UI 015 — creates the <c>cnas.BulkSelections</c> +
    /// <c>cnas.BulkOperationRuns</c> tables backing the server-side cross-page
    /// bulk-action stack. Selections persist registry + filter envelope + include /
    /// exclude id lists; runs record one execution of a registered bulk operation
    /// against a selection.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    ///   <item><description>
    ///     <c>BulkSelections.ExplicitIncludeIds</c> / <c>ExplicitExcludeIds</c> are
    ///     <c>jsonb</c> arrays of <see cref="long"/> persisted via an EF value
    ///     converter; the column defaults to an empty array when the caller
    ///     supplies no overlay.
    ///   </description></item>
    ///   <item><description>
    ///     <c>BulkOperationRuns</c> has a partial unique index over
    ///     <c>(ActorUserId, OperationCode, IdempotencyKey)</c> filtered to
    ///     <c>IdempotencyKey IS NOT NULL</c> — the idempotency natural key. Rows
    ///     with no key bypass de-duplication.
    ///   </description></item>
    ///   <item><description>
    ///     <c>BulkSelections</c> has a composite index over
    ///     <c>(IsConsumed, ExpiresAtUtc)</c> supporting the daily cleanup-job
    ///     predicate "find rows past the grace window".
    ///   </description></item>
    ///   <item><description>
    ///     No foreign keys between the two tables — the application enforces
    ///     <c>BulkOperationRun.BulkSelectionId</c> → <c>BulkSelections.Id</c>
    ///     semantics in the runner. Avoiding the FK keeps the cleanup job's hard
    ///     delete simple (no need to cascade-disable runs).
    ///   </description></item>
    /// </list>
    /// </remarks>
    public partial class AddBulkActionTables : Migration
    {
        /// <summary>Creates the two tables and their indexes.</summary>
        /// <param name="migrationBuilder">EF Core migration builder.</param>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.CreateTable(
                name: "BulkOperationRuns",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BulkSelectionId = table.Column<long>(type: "bigint", nullable: false),
                    OperationCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ActorUserId = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    TotalRows = table.Column<int>(type: "integer", nullable: false),
                    SucceededRows = table.Column<int>(type: "integer", nullable: false),
                    FailedRows = table.Column<int>(type: "integer", nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ParametersJson = table.Column<string>(type: "text", nullable: true),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    FailureSummaryJson = table.Column<string>(type: "text", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BulkOperationRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BulkSelections",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Registry = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    OwnerUserId = table.Column<long>(type: "bigint", nullable: false),
                    FilterJson = table.Column<string>(type: "text", nullable: false),
                    ExplicitIncludeIds = table.Column<string>(type: "jsonb", nullable: false),
                    ExplicitExcludeIds = table.Column<string>(type: "jsonb", nullable: false),
                    ResolvedCount = table.Column<int>(type: "integer", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsConsumed = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BulkSelections", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BulkOperationRuns_ActorUserId",
                schema: "cnas",
                table: "BulkOperationRuns",
                column: "ActorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_BulkOperationRuns_ActorUserId_OperationCode_IdempotencyKey",
                schema: "cnas",
                table: "BulkOperationRuns",
                columns: new[] { "ActorUserId", "OperationCode", "IdempotencyKey" },
                unique: true,
                filter: "\"IdempotencyKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_BulkOperationRuns_BulkSelectionId",
                schema: "cnas",
                table: "BulkOperationRuns",
                column: "BulkSelectionId");

            migrationBuilder.CreateIndex(
                name: "IX_BulkOperationRuns_CreatedAtUtc",
                schema: "cnas",
                table: "BulkOperationRuns",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_BulkOperationRuns_IsActive",
                schema: "cnas",
                table: "BulkOperationRuns",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_BulkSelections_CreatedAtUtc",
                schema: "cnas",
                table: "BulkSelections",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_BulkSelections_IsActive",
                schema: "cnas",
                table: "BulkSelections",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_BulkSelections_IsConsumed_ExpiresAtUtc",
                schema: "cnas",
                table: "BulkSelections",
                columns: new[] { "IsConsumed", "ExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_BulkSelections_OwnerUserId",
                schema: "cnas",
                table: "BulkSelections",
                column: "OwnerUserId");
        }

        /// <summary>Drops both tables cleanly — there are no foreign keys to cascade.</summary>
        /// <param name="migrationBuilder">EF Core migration builder.</param>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropTable(
                name: "BulkOperationRuns",
                schema: "cnas");

            migrationBuilder.DropTable(
                name: "BulkSelections",
                schema: "cnas");
        }
    }
}
