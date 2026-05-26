using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAbacRegistry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AbacRuleSets",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PolicyName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    DefaultEffect = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    RegisteredByUserId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AbacRuleSets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AbacRules",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RuleSetId = table.Column<long>(type: "bigint", nullable: false),
                    OrderIndex = table.Column<int>(type: "integer", nullable: false),
                    Effect = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ConditionExpression = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AbacRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AbacRules_AbacRuleSets_RuleSetId",
                        column: x => x.RuleSetId,
                        principalSchema: "cnas",
                        principalTable: "AbacRuleSets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AbacRules_CreatedAtUtc",
                schema: "cnas",
                table: "AbacRules",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AbacRules_IsActive",
                schema: "cnas",
                table: "AbacRules",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_AbacRules_RuleSetId_IsActive_OrderIndex",
                schema: "cnas",
                table: "AbacRules",
                columns: new[] { "RuleSetId", "IsActive", "OrderIndex" });

            migrationBuilder.CreateIndex(
                name: "UX_AbacRules_RuleSetId_OrderIndex_Active",
                schema: "cnas",
                table: "AbacRules",
                columns: new[] { "RuleSetId", "OrderIndex" },
                unique: true,
                filter: "\"IsActive\" = true");

            migrationBuilder.CreateIndex(
                name: "IX_AbacRuleSets_CreatedAtUtc",
                schema: "cnas",
                table: "AbacRuleSets",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AbacRuleSets_IsActive",
                schema: "cnas",
                table: "AbacRuleSets",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_AbacRuleSets_IsActive_PolicyName",
                schema: "cnas",
                table: "AbacRuleSets",
                columns: new[] { "IsActive", "PolicyName" });

            migrationBuilder.CreateIndex(
                name: "UX_AbacRuleSets_PolicyName",
                schema: "cnas",
                table: "AbacRuleSets",
                column: "PolicyName",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AbacRules",
                schema: "cnas");

            migrationBuilder.DropTable(
                name: "AbacRuleSets",
                schema: "cnas");
        }
    }
}
