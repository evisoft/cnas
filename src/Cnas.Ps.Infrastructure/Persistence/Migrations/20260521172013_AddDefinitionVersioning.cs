using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// R0129 / R0142 / TOR CF 15.04 — adds the definition-versioning columns to
    /// <c>WorkflowDefinitions</c> and <c>ServicePassports</c>, plus the pinned-version
    /// columns on <c>ServiceApplications</c>. The previous global unique index on
    /// <c>ServicePassports.Code</c> is replaced by two new indexes: <c>(Code, Version)</c>
    /// unique as the natural-key safety net, plus a partial unique index on <c>(Code)
    /// WHERE IsCurrent = true</c> that enforces "at most one current row per code" at the
    /// database layer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Data backfill.</b> The defaults on the new columns
    /// (<c>Version=1</c>, <c>IsCurrent=true</c>, <c>PinnedServicePassportVersion=1</c>,
    /// <c>PinnedWorkflowVersion=1</c>) take care of every pre-existing row. The chain
    /// columns (<c>Supersedes*Id</c> / <c>SupersededBy*Id</c> / <c>SupersededAtUtc</c>)
    /// are nullable and remain NULL on the pre-existing rows — there is no history yet.
    /// No PL/pgSQL <c>DO</c> block is required.
    /// </para>
    /// <para>
    /// <b>Reversibility.</b> <see cref="Down"/> drops the new columns and restores the
    /// old single-column unique index on <c>Code</c>; it will FAIL if multiple version
    /// rows for the same code already exist (the rollback path is documented as a
    /// dev-only escape hatch — production rollbacks of this migration are unsupported
    /// once a new version row has been published).
    /// </para>
    /// </remarks>
    public partial class AddDefinitionVersioning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);
            migrationBuilder.DropIndex(
                name: "IX_ServicePassports_Code",
                schema: "cnas",
                table: "ServicePassports");

            migrationBuilder.AddColumn<DateTime>(
                name: "SupersededAtUtc",
                schema: "cnas",
                table: "WorkflowDefinitions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SupersededByDefinitionId",
                schema: "cnas",
                table: "WorkflowDefinitions",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SupersedesDefinitionId",
                schema: "cnas",
                table: "WorkflowDefinitions",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsCurrent",
                schema: "cnas",
                table: "ServicePassports",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SupersededAtUtc",
                schema: "cnas",
                table: "ServicePassports",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SupersededByPassportId",
                schema: "cnas",
                table: "ServicePassports",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SupersedesPassportId",
                schema: "cnas",
                table: "ServicePassports",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                schema: "cnas",
                table: "ServicePassports",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "PinnedServicePassportVersion",
                schema: "cnas",
                table: "ServiceApplications",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "PinnedWorkflowVersion",
                schema: "cnas",
                table: "ServiceApplications",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateIndex(
                name: "IX_ServicePassports_Code_Current",
                schema: "cnas",
                table: "ServicePassports",
                column: "Code",
                unique: true,
                filter: "\"IsCurrent\" = true");

            migrationBuilder.CreateIndex(
                name: "IX_ServicePassports_Code_Version",
                schema: "cnas",
                table: "ServicePassports",
                columns: new[] { "Code", "Version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);
            migrationBuilder.DropIndex(
                name: "IX_ServicePassports_Code_Current",
                schema: "cnas",
                table: "ServicePassports");

            migrationBuilder.DropIndex(
                name: "IX_ServicePassports_Code_Version",
                schema: "cnas",
                table: "ServicePassports");

            migrationBuilder.DropColumn(
                name: "SupersededAtUtc",
                schema: "cnas",
                table: "WorkflowDefinitions");

            migrationBuilder.DropColumn(
                name: "SupersededByDefinitionId",
                schema: "cnas",
                table: "WorkflowDefinitions");

            migrationBuilder.DropColumn(
                name: "SupersedesDefinitionId",
                schema: "cnas",
                table: "WorkflowDefinitions");

            migrationBuilder.DropColumn(
                name: "IsCurrent",
                schema: "cnas",
                table: "ServicePassports");

            migrationBuilder.DropColumn(
                name: "SupersededAtUtc",
                schema: "cnas",
                table: "ServicePassports");

            migrationBuilder.DropColumn(
                name: "SupersededByPassportId",
                schema: "cnas",
                table: "ServicePassports");

            migrationBuilder.DropColumn(
                name: "SupersedesPassportId",
                schema: "cnas",
                table: "ServicePassports");

            migrationBuilder.DropColumn(
                name: "Version",
                schema: "cnas",
                table: "ServicePassports");

            migrationBuilder.DropColumn(
                name: "PinnedServicePassportVersion",
                schema: "cnas",
                table: "ServiceApplications");

            migrationBuilder.DropColumn(
                name: "PinnedWorkflowVersion",
                schema: "cnas",
                table: "ServiceApplications");

            migrationBuilder.CreateIndex(
                name: "IX_ServicePassports_Code",
                schema: "cnas",
                table: "ServicePassports",
                column: "Code",
                unique: true);
        }
    }
}
