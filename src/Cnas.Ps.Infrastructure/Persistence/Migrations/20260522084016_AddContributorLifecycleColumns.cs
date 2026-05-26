using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// R0305 / TOR Annex 1 — adds the lifecycle columns underpinning the 9 Contributor
    /// business processes: <c>IsDeactivated</c>, <c>DeactivatedAtUtc</c>,
    /// <c>DeactivationReason</c> (BP 1.3/1.4); <c>IsDeceased</c>, <c>DeceasedAtUtc</c>,
    /// <c>IsDissolved</c>, <c>DissolvedAtUtc</c> (BP 1.9); <c>MergedIntoContributorId</c>
    /// (BP 1.5); <c>CnasBranchCode</c> (BP 1.8). Three supporting indexes back the new
    /// query patterns (active-only filter, "list duplicates merged into X" reverse
    /// navigation, and "list contributors at branch X" for the bulk reassignment op).
    /// </summary>
    public partial class AddContributorLifecycleColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CnasBranchCode",
                schema: "cnas",
                table: "Contributors",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeactivatedAtUtc",
                schema: "cnas",
                table: "Contributors",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeactivationReason",
                schema: "cnas",
                table: "Contributors",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeceasedAtUtc",
                schema: "cnas",
                table: "Contributors",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DissolvedAtUtc",
                schema: "cnas",
                table: "Contributors",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeactivated",
                schema: "cnas",
                table: "Contributors",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeceased",
                schema: "cnas",
                table: "Contributors",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsDissolved",
                schema: "cnas",
                table: "Contributors",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "MergedIntoContributorId",
                schema: "cnas",
                table: "Contributors",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Contributors_CnasBranchCode",
                schema: "cnas",
                table: "Contributors",
                column: "CnasBranchCode");

            migrationBuilder.CreateIndex(
                name: "IX_Contributors_IsDeactivated",
                schema: "cnas",
                table: "Contributors",
                column: "IsDeactivated");

            migrationBuilder.CreateIndex(
                name: "IX_Contributors_MergedIntoContributorId",
                schema: "cnas",
                table: "Contributors",
                column: "MergedIntoContributorId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Contributors_CnasBranchCode",
                schema: "cnas",
                table: "Contributors");

            migrationBuilder.DropIndex(
                name: "IX_Contributors_IsDeactivated",
                schema: "cnas",
                table: "Contributors");

            migrationBuilder.DropIndex(
                name: "IX_Contributors_MergedIntoContributorId",
                schema: "cnas",
                table: "Contributors");

            migrationBuilder.DropColumn(
                name: "CnasBranchCode",
                schema: "cnas",
                table: "Contributors");

            migrationBuilder.DropColumn(
                name: "DeactivatedAtUtc",
                schema: "cnas",
                table: "Contributors");

            migrationBuilder.DropColumn(
                name: "DeactivationReason",
                schema: "cnas",
                table: "Contributors");

            migrationBuilder.DropColumn(
                name: "DeceasedAtUtc",
                schema: "cnas",
                table: "Contributors");

            migrationBuilder.DropColumn(
                name: "DissolvedAtUtc",
                schema: "cnas",
                table: "Contributors");

            migrationBuilder.DropColumn(
                name: "IsDeactivated",
                schema: "cnas",
                table: "Contributors");

            migrationBuilder.DropColumn(
                name: "IsDeceased",
                schema: "cnas",
                table: "Contributors");

            migrationBuilder.DropColumn(
                name: "IsDissolved",
                schema: "cnas",
                table: "Contributors");

            migrationBuilder.DropColumn(
                name: "MergedIntoContributorId",
                schema: "cnas",
                table: "Contributors");
        }
    }
}
