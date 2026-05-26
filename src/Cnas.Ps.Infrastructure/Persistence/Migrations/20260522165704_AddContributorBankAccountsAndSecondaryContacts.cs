using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddContributorBankAccountsAndSecondaryContacts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PayerBankAccounts",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PayerId = table.Column<long>(type: "bigint", nullable: false),
                    AccountHolderName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Iban = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    IbanHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    BankName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    BankBic = table.Column<string>(type: "character varying(11)", maxLength: 11, nullable: false),
                    IsPrimary = table.Column<bool>(type: "boolean", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false, defaultValue: "MDL"),
                    ValidFromUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ValidToUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ChangeReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    RecordedByUserSqid = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PayerBankAccounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PayerSecondaryContacts",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PayerId = table.Column<long>(type: "bigint", nullable: false),
                    ContactPersonName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Role = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    PhoneE164 = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    ValidFromUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ValidToUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ChangeReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    RecordedByUserSqid = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PayerSecondaryContacts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PayerBankAccounts_CreatedAtUtc",
                schema: "cnas",
                table: "PayerBankAccounts",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_PayerBankAccounts_IbanHash",
                schema: "cnas",
                table: "PayerBankAccounts",
                column: "IbanHash");

            migrationBuilder.CreateIndex(
                name: "IX_PayerBankAccounts_IsActive",
                schema: "cnas",
                table: "PayerBankAccounts",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_PayerBankAccounts_ValidFromUtc",
                schema: "cnas",
                table: "PayerBankAccounts",
                column: "ValidFromUtc");

            migrationBuilder.CreateIndex(
                name: "UX_PayerBankAccounts_CurrentIban",
                schema: "cnas",
                table: "PayerBankAccounts",
                columns: new[] { "PayerId", "IbanHash" },
                unique: true,
                filter: "\"ValidToUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "UX_PayerBankAccounts_CurrentPrimary",
                schema: "cnas",
                table: "PayerBankAccounts",
                column: "PayerId",
                unique: true,
                filter: "\"ValidToUtc\" IS NULL AND \"IsPrimary\" = TRUE");

            migrationBuilder.CreateIndex(
                name: "IX_PayerSecondaryContacts_CreatedAtUtc",
                schema: "cnas",
                table: "PayerSecondaryContacts",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_PayerSecondaryContacts_IsActive",
                schema: "cnas",
                table: "PayerSecondaryContacts",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_PayerSecondaryContacts_PayerId",
                schema: "cnas",
                table: "PayerSecondaryContacts",
                column: "PayerId");

            migrationBuilder.CreateIndex(
                name: "IX_PayerSecondaryContacts_ValidFromUtc",
                schema: "cnas",
                table: "PayerSecondaryContacts",
                column: "ValidFromUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PayerBankAccounts",
                schema: "cnas");

            migrationBuilder.DropTable(
                name: "PayerSecondaryContacts",
                schema: "cnas");
        }
    }
}
