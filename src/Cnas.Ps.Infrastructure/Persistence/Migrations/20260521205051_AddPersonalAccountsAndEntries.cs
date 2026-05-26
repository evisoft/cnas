using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// R0516 / TOR CF 02.04 — adds the citizen personal-account aggregate
    /// (<c>cnas.PersonalAccounts</c>) plus the per-month contribution-entries
    /// table (<c>cnas.PersonalAccountEntries</c>) that backs the authenticated
    /// extract endpoint. No seed data — accounts are created lazily by the
    /// application layer when a Solicitant first crosses the CNAS records.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Schema changes (Up).</b> Creates the two tables with the standard
    /// auditable-entity convention (xmin concurrency token, soft-delete via
    /// <c>IsActive</c>). The PersonalAccounts table is keyed by the surrogate
    /// id; a unique index on <c>OwnerSolicitantId</c> enforces one account per
    /// Solicitant, and a unique index on <c>AccountCode</c> protects the
    /// stable external code. The PersonalAccountEntries table carries a
    /// composite unique index on <c>(PersonalAccountId, Year, Month, SourceCode)</c>
    /// — the natural key documented on the entity remarks.
    /// </para>
    /// <para>
    /// <b>No foreign-key constraint.</b> The application layer enforces the
    /// FK from PersonalAccountEntry to PersonalAccount via the indexed
    /// <c>PersonalAccountId</c> column; explicit DB-level cascades are
    /// deliberately omitted so the soft-delete sweep can mark account rows
    /// inactive without orphaning entries.
    /// </para>
    /// <para>
    /// <b>Down.</b> Drops both tables (and their indexes by cascade). Safe —
    /// neither table is referenced by other tables in this revision.
    /// </para>
    /// </remarks>
    public partial class AddPersonalAccountsAndEntries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);


            migrationBuilder.CreateTable(
                name: "PersonalAccountEntries",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PersonalAccountId = table.Column<long>(type: "bigint", nullable: false),
                    Year = table.Column<int>(type: "integer", nullable: false),
                    Month = table.Column<int>(type: "integer", nullable: false),
                    ContributionBaseAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    ContributionPaidAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    SourceCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PersonalAccountEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PersonalAccounts",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OwnerSolicitantId = table.Column<long>(type: "bigint", nullable: false),
                    AccountCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    LifetimeContributions = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    LifetimeMonths = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PersonalAccounts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PersonalAccountEntries_CreatedAtUtc",
                schema: "cnas",
                table: "PersonalAccountEntries",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_PersonalAccountEntries_IsActive",
                schema: "cnas",
                table: "PersonalAccountEntries",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_PersonalAccountEntries_PersonalAccountId",
                schema: "cnas",
                table: "PersonalAccountEntries",
                column: "PersonalAccountId");

            migrationBuilder.CreateIndex(
                name: "UX_PersonalAccountEntries_NaturalKey",
                schema: "cnas",
                table: "PersonalAccountEntries",
                columns: new[] { "PersonalAccountId", "Year", "Month", "SourceCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PersonalAccounts_AccountCode",
                schema: "cnas",
                table: "PersonalAccounts",
                column: "AccountCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PersonalAccounts_CreatedAtUtc",
                schema: "cnas",
                table: "PersonalAccounts",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_PersonalAccounts_IsActive",
                schema: "cnas",
                table: "PersonalAccounts",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_PersonalAccounts_OwnerSolicitantId",
                schema: "cnas",
                table: "PersonalAccounts",
                column: "OwnerSolicitantId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropTable(
                name: "PersonalAccountEntries",
                schema: "cnas");

            migrationBuilder.DropTable(
                name: "PersonalAccounts",
                schema: "cnas");
        }
    }
}
