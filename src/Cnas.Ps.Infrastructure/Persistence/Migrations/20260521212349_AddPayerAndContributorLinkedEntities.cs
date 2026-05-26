using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPayerAndContributorLinkedEntities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ContributorActivityPeriods",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ContributorId = table.Column<long>(type: "bigint", nullable: false),
                    EmployerCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Position = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    MonthlySalary = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
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
                    table.PrimaryKey("PK_ContributorActivityPeriods", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ContributorAddresses",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ContributorId = table.Column<long>(type: "bigint", nullable: false),
                    Street = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    City = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Region = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PostalCode = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Country = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false, defaultValue: "MD"),
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
                    table.PrimaryKey("PK_ContributorAddresses", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ContributorCivilStatuses",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ContributorId = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    EffectiveDate = table.Column<DateOnly>(type: "date", nullable: true),
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
                    table.PrimaryKey("PK_ContributorCivilStatuses", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ContributorContacts",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ContributorId = table.Column<long>(type: "bigint", nullable: false),
                    PhoneE164 = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    ContactPersonName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
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
                    table.PrimaryKey("PK_ContributorContacts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ContributorPre1999PeriodCarnetMunca",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ContributorId = table.Column<long>(type: "bigint", nullable: false),
                    CarnetMuncaNumber = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    PeriodStartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    PeriodEndDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EmployerName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Position = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
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
                    table.PrimaryKey("PK_ContributorPre1999PeriodCarnetMunca", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ContributorSocialInsuranceContracts",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ContributorId = table.Column<long>(type: "bigint", nullable: false),
                    ContractNumber = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ContractStartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ContractEndDate = table.Column<DateOnly>(type: "date", nullable: true),
                    MonthlyContributionAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    CounterpartyName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
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
                    table.PrimaryKey("PK_ContributorSocialInsuranceContracts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PayerActivityCAEM",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PayerId = table.Column<long>(type: "bigint", nullable: false),
                    CaemCode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CaemDescription = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    IsPrimary = table.Column<bool>(type: "boolean", nullable: false),
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
                    table.PrimaryKey("PK_PayerActivityCAEM", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PayerAddresses",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PayerId = table.Column<long>(type: "bigint", nullable: false),
                    Street = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    City = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Region = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PostalCode = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Country = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false, defaultValue: "MD"),
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
                    table.PrimaryKey("PK_PayerAddresses", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PayerContacts",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PayerId = table.Column<long>(type: "bigint", nullable: false),
                    PhoneE164 = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    ContactPersonName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
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
                    table.PrimaryKey("PK_PayerContacts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PayerHistory",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PayerId = table.Column<long>(type: "bigint", nullable: false),
                    FieldName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    OldValue = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    NewValue = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ChangeReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ChangedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
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
                    table.PrimaryKey("PK_PayerHistory", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ContributorActivityPeriods_ContributorId",
                schema: "cnas",
                table: "ContributorActivityPeriods",
                column: "ContributorId");

            migrationBuilder.CreateIndex(
                name: "IX_ContributorActivityPeriods_CreatedAtUtc",
                schema: "cnas",
                table: "ContributorActivityPeriods",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ContributorActivityPeriods_IsActive",
                schema: "cnas",
                table: "ContributorActivityPeriods",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_ContributorActivityPeriods_ValidFromUtc",
                schema: "cnas",
                table: "ContributorActivityPeriods",
                column: "ValidFromUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ContributorAddresses_CreatedAtUtc",
                schema: "cnas",
                table: "ContributorAddresses",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ContributorAddresses_IsActive",
                schema: "cnas",
                table: "ContributorAddresses",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_ContributorAddresses_ValidFromUtc",
                schema: "cnas",
                table: "ContributorAddresses",
                column: "ValidFromUtc");

            migrationBuilder.CreateIndex(
                name: "UX_ContributorAddresses_CurrentRow",
                schema: "cnas",
                table: "ContributorAddresses",
                column: "ContributorId",
                unique: true,
                filter: "\"ValidToUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ContributorCivilStatuses_CreatedAtUtc",
                schema: "cnas",
                table: "ContributorCivilStatuses",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ContributorCivilStatuses_IsActive",
                schema: "cnas",
                table: "ContributorCivilStatuses",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_ContributorCivilStatuses_ValidFromUtc",
                schema: "cnas",
                table: "ContributorCivilStatuses",
                column: "ValidFromUtc");

            migrationBuilder.CreateIndex(
                name: "UX_ContributorCivilStatuses_CurrentRow",
                schema: "cnas",
                table: "ContributorCivilStatuses",
                column: "ContributorId",
                unique: true,
                filter: "\"ValidToUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ContributorContacts_CreatedAtUtc",
                schema: "cnas",
                table: "ContributorContacts",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ContributorContacts_IsActive",
                schema: "cnas",
                table: "ContributorContacts",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_ContributorContacts_ValidFromUtc",
                schema: "cnas",
                table: "ContributorContacts",
                column: "ValidFromUtc");

            migrationBuilder.CreateIndex(
                name: "UX_ContributorContacts_CurrentRow",
                schema: "cnas",
                table: "ContributorContacts",
                column: "ContributorId",
                unique: true,
                filter: "\"ValidToUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ContributorPre1999PeriodCarnetMunca_ContributorId",
                schema: "cnas",
                table: "ContributorPre1999PeriodCarnetMunca",
                column: "ContributorId");

            migrationBuilder.CreateIndex(
                name: "IX_ContributorPre1999PeriodCarnetMunca_CreatedAtUtc",
                schema: "cnas",
                table: "ContributorPre1999PeriodCarnetMunca",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ContributorPre1999PeriodCarnetMunca_IsActive",
                schema: "cnas",
                table: "ContributorPre1999PeriodCarnetMunca",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_ContributorPre1999PeriodCarnetMunca_PeriodStartDate",
                schema: "cnas",
                table: "ContributorPre1999PeriodCarnetMunca",
                column: "PeriodStartDate");

            migrationBuilder.CreateIndex(
                name: "IX_ContributorSocialInsuranceContracts_CreatedAtUtc",
                schema: "cnas",
                table: "ContributorSocialInsuranceContracts",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ContributorSocialInsuranceContracts_IsActive",
                schema: "cnas",
                table: "ContributorSocialInsuranceContracts",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_ContributorSocialInsuranceContracts_ValidFromUtc",
                schema: "cnas",
                table: "ContributorSocialInsuranceContracts",
                column: "ValidFromUtc");

            migrationBuilder.CreateIndex(
                name: "UX_ContributorSocialInsuranceContracts_CurrentRow",
                schema: "cnas",
                table: "ContributorSocialInsuranceContracts",
                column: "ContributorId",
                unique: true,
                filter: "\"ValidToUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PayerActivityCAEM_CreatedAtUtc",
                schema: "cnas",
                table: "PayerActivityCAEM",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_PayerActivityCAEM_IsActive",
                schema: "cnas",
                table: "PayerActivityCAEM",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_PayerActivityCAEM_PayerId",
                schema: "cnas",
                table: "PayerActivityCAEM",
                column: "PayerId");

            migrationBuilder.CreateIndex(
                name: "IX_PayerActivityCAEM_ValidFromUtc",
                schema: "cnas",
                table: "PayerActivityCAEM",
                column: "ValidFromUtc");

            migrationBuilder.CreateIndex(
                name: "UX_PayerActivityCAEM_CurrentRow",
                schema: "cnas",
                table: "PayerActivityCAEM",
                columns: new[] { "PayerId", "CaemCode" },
                unique: true,
                filter: "\"ValidToUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PayerAddresses_CreatedAtUtc",
                schema: "cnas",
                table: "PayerAddresses",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_PayerAddresses_IsActive",
                schema: "cnas",
                table: "PayerAddresses",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_PayerAddresses_ValidFromUtc",
                schema: "cnas",
                table: "PayerAddresses",
                column: "ValidFromUtc");

            migrationBuilder.CreateIndex(
                name: "UX_PayerAddresses_CurrentRow",
                schema: "cnas",
                table: "PayerAddresses",
                column: "PayerId",
                unique: true,
                filter: "\"ValidToUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PayerContacts_CreatedAtUtc",
                schema: "cnas",
                table: "PayerContacts",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_PayerContacts_IsActive",
                schema: "cnas",
                table: "PayerContacts",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_PayerContacts_ValidFromUtc",
                schema: "cnas",
                table: "PayerContacts",
                column: "ValidFromUtc");

            migrationBuilder.CreateIndex(
                name: "UX_PayerContacts_CurrentRow",
                schema: "cnas",
                table: "PayerContacts",
                column: "PayerId",
                unique: true,
                filter: "\"ValidToUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PayerHistory_CreatedAtUtc",
                schema: "cnas",
                table: "PayerHistory",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_PayerHistory_IsActive",
                schema: "cnas",
                table: "PayerHistory",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_PayerHistory_PayerId_ChangedAtUtcDesc",
                schema: "cnas",
                table: "PayerHistory",
                columns: new[] { "PayerId", "ChangedAtUtc" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ContributorActivityPeriods",
                schema: "cnas");

            migrationBuilder.DropTable(
                name: "ContributorAddresses",
                schema: "cnas");

            migrationBuilder.DropTable(
                name: "ContributorCivilStatuses",
                schema: "cnas");

            migrationBuilder.DropTable(
                name: "ContributorContacts",
                schema: "cnas");

            migrationBuilder.DropTable(
                name: "ContributorPre1999PeriodCarnetMunca",
                schema: "cnas");

            migrationBuilder.DropTable(
                name: "ContributorSocialInsuranceContracts",
                schema: "cnas");

            migrationBuilder.DropTable(
                name: "PayerActivityCAEM",
                schema: "cnas");

            migrationBuilder.DropTable(
                name: "PayerAddresses",
                schema: "cnas");

            migrationBuilder.DropTable(
                name: "PayerContacts",
                schema: "cnas");

            migrationBuilder.DropTable(
                name: "PayerHistory",
                schema: "cnas");
        }
    }
}
