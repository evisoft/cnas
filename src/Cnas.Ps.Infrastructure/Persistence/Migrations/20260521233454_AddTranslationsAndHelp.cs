using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTranslationsAndHelp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HelpTopics",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Code = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Module = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AnchorSelector = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HelpTopics", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TranslationKeys",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Code = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Description = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    Module = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TranslationKeys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "HelpTopicTranslations",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    HelpTopicId = table.Column<long>(type: "bigint", nullable: false),
                    Language = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    BodyMarkdown = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: false),
                    IsApproved = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    TranslatorNote = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HelpTopicTranslations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HelpTopicTranslations_HelpTopics_HelpTopicId",
                        column: x => x.HelpTopicId,
                        principalSchema: "cnas",
                        principalTable: "HelpTopics",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TranslationValues",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TranslationKeyId = table.Column<long>(type: "bigint", nullable: false),
                    Language = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Text = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    IsApproved = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    TranslatorNote = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TranslationValues", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TranslationValues_TranslationKeys_TranslationKeyId",
                        column: x => x.TranslationKeyId,
                        principalSchema: "cnas",
                        principalTable: "TranslationKeys",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HelpTopics_Code",
                schema: "cnas",
                table: "HelpTopics",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HelpTopics_CreatedAtUtc",
                schema: "cnas",
                table: "HelpTopics",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_HelpTopics_IsActive",
                schema: "cnas",
                table: "HelpTopics",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_HelpTopics_Module",
                schema: "cnas",
                table: "HelpTopics",
                column: "Module");

            migrationBuilder.CreateIndex(
                name: "IX_HelpTopicTranslations_CreatedAtUtc",
                schema: "cnas",
                table: "HelpTopicTranslations",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_HelpTopicTranslations_HelpTopicId_Language",
                schema: "cnas",
                table: "HelpTopicTranslations",
                columns: new[] { "HelpTopicId", "Language" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HelpTopicTranslations_IsActive",
                schema: "cnas",
                table: "HelpTopicTranslations",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_TranslationKeys_Code",
                schema: "cnas",
                table: "TranslationKeys",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TranslationKeys_CreatedAtUtc",
                schema: "cnas",
                table: "TranslationKeys",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_TranslationKeys_IsActive",
                schema: "cnas",
                table: "TranslationKeys",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_TranslationKeys_Module",
                schema: "cnas",
                table: "TranslationKeys",
                column: "Module");

            migrationBuilder.CreateIndex(
                name: "IX_TranslationValues_CreatedAtUtc",
                schema: "cnas",
                table: "TranslationValues",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_TranslationValues_IsActive",
                schema: "cnas",
                table: "TranslationValues",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_TranslationValues_TranslationKeyId_Language",
                schema: "cnas",
                table: "TranslationValues",
                columns: new[] { "TranslationKeyId", "Language" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HelpTopicTranslations",
                schema: "cnas");

            migrationBuilder.DropTable(
                name: "TranslationValues",
                schema: "cnas");

            migrationBuilder.DropTable(
                name: "HelpTopics",
                schema: "cnas");

            migrationBuilder.DropTable(
                name: "TranslationKeys",
                schema: "cnas");
        }
    }
}
