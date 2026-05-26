using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// R0053 / SEC 018 — Creates <c>cnas.RefreshTokens</c>, the opaque-token store
    /// behind the JWT access + refresh-token pipeline (CLAUDE.md §5.3). One row per
    /// refresh token, hashed at rest (SHA-256, 64 hex chars); a logout or
    /// reuse-detected event revokes every row sharing the same <c>FamilyId</c>.
    /// See <see cref="Cnas.Ps.Core.Domain.RefreshToken"/> for the rotation /
    /// reuse-detection contract.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Five indexes are created (three contributed here + two by
    /// <see cref="Cnas.Ps.Infrastructure.Persistence.Configurations.AuditableEntityConfiguration{TEntity}"/>):
    /// <list type="bullet">
    ///   <item><description><c>UNIQUE(TokenHash)</c> — every refresh-token hash is unique; backs lookups and prevents duplicate inserts.</description></item>
    ///   <item><description><c>(FamilyId, ConsumedAtUtc)</c> — supports the family-revoke + reuse-detection queries.</description></item>
    ///   <item><description><c>(UserId)</c> — supports per-user session listings.</description></item>
    /// </list>
    /// </para>
    /// </remarks>
    public partial class AddRefreshTokensTable : Migration
    {
        /// <summary>Creates <c>cnas.RefreshTokens</c> with the unique-hash + family/user indexes.</summary>
        /// <param name="migrationBuilder">EF Core migration builder.</param>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RefreshTokens",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FamilyId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParentTokenId = table.Column<long>(type: "bigint", nullable: true),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    IssuedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConsumedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RevokedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RevokedReason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RefreshTokens", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RefreshTokens_CreatedAtUtc",
                schema: "cnas",
                table: "RefreshTokens",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_RefreshTokens_FamilyId_ConsumedAtUtc",
                schema: "cnas",
                table: "RefreshTokens",
                columns: new[] { "FamilyId", "ConsumedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_RefreshTokens_IsActive",
                schema: "cnas",
                table: "RefreshTokens",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_RefreshTokens_TokenHash",
                schema: "cnas",
                table: "RefreshTokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RefreshTokens_UserId",
                schema: "cnas",
                table: "RefreshTokens",
                column: "UserId");
        }

        /// <summary>Drops <c>cnas.RefreshTokens</c> and all its indexes.</summary>
        /// <param name="migrationBuilder">EF Core migration builder.</param>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RefreshTokens",
                schema: "cnas");
        }
    }
}
