using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Creates the <c>cnas.PendingAdminActions</c> table that backs
    /// <see cref="Cnas.Ps.Core.Domain.PendingAdminAction"/> — the authoritative store for
    /// sensitive admin actions waiting on a second-administrator approval
    /// (R0058 / SEC 027 — maker-checker / 4-eyes). Two administrators are required to land
    /// any gated admin action: the maker submits, and any other administrator visits
    /// <c>/api/admin/pending-actions/{id}/approve|reject</c> to decide.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Six indexes are created (two contributed by
    /// <see cref="Cnas.Ps.Infrastructure.Persistence.Configurations.AuditableEntityConfiguration{TEntity}"/>
    /// — <c>IsActive</c> and <c>CreatedAtUtc</c> — plus four domain-specific ones):
    /// <list type="bullet">
    ///   <item><description><c>(Status)</c> — supports the "list pending" query and the expiry sweeper.</description></item>
    ///   <item><description><c>(ExpiresAtUtc)</c> — supports the expiry sweeper's <c>WHERE ExpiresAtUtc &lt; now</c>.</description></item>
    ///   <item><description><c>(MakerUserId)</c> — supports "actions I submitted" lookups.</description></item>
    ///   <item><description><c>(CheckerUserId)</c> — supports "actions I approved" lookups.</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// Down migration drops the table cleanly — no foreign keys cross into or out of this
    /// row (maker/checker user references are raw <c>bigint</c> columns rather than EF
    /// navigations so the maker-checker lifecycle is decoupled from the user-profile
    /// schema's future evolution).
    /// </para>
    /// </remarks>
    public partial class AddPendingAdminActionsTable : Migration
    {
        /// <summary>
        /// Creates <c>cnas.PendingAdminActions</c> with its six indexes (status, expiry,
        /// maker, checker, soft-delete, audit-timestamp).
        /// </summary>
        /// <param name="migrationBuilder">EF Core migration builder.</param>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PendingAdminActions",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Operation = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PayloadJson = table.Column<string>(type: "text", nullable: false),
                    MakerUserId = table.Column<long>(type: "bigint", nullable: false),
                    MakerRequestedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CheckerUserId = table.Column<long>(type: "bigint", nullable: true),
                    CheckerDecidedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    RejectionReason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingAdminActions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PendingAdminActions_CheckerUserId",
                schema: "cnas",
                table: "PendingAdminActions",
                column: "CheckerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PendingAdminActions_CreatedAtUtc",
                schema: "cnas",
                table: "PendingAdminActions",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_PendingAdminActions_ExpiresAtUtc",
                schema: "cnas",
                table: "PendingAdminActions",
                column: "ExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_PendingAdminActions_IsActive",
                schema: "cnas",
                table: "PendingAdminActions",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_PendingAdminActions_MakerUserId",
                schema: "cnas",
                table: "PendingAdminActions",
                column: "MakerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PendingAdminActions_Status",
                schema: "cnas",
                table: "PendingAdminActions",
                column: "Status");
        }

        /// <summary>Drops <c>cnas.PendingAdminActions</c> and all its indexes.</summary>
        /// <param name="migrationBuilder">EF Core migration builder.</param>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PendingAdminActions",
                schema: "cnas");
        }
    }
}
