using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "cnas");

            migrationBuilder.CreateTable(
                name: "AuditLogs",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EventAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Severity = table.Column<int>(type: "integer", nullable: false),
                    EventCode = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ActorId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    TargetEntity = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    TargetEntityId = table.Column<long>(type: "bigint", nullable: true),
                    SourceIp = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CorrelationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DetailsJson = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Classifiers",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    LabelRo = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    LabelEn = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    LabelRu = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ParentCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Classifiers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Contributors",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Idno = table.Column<string>(type: "character varying(13)", maxLength: 13, nullable: false),
                    Denumire = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    CfojCode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    CaemCode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    IsInsolvent = table.Column<bool>(type: "boolean", nullable: false),
                    RegisteredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeregisteredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpstreamRsudId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Contributors", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Documents",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DossierId = table.Column<long>(type: "bigint", nullable: true),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    MimeType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    StorageObjectKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    StorageBucket = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ContentSha256Hex = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IsSigned = table.Column<bool>(type: "boolean", nullable: false),
                    SignatureObjectKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Documents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InsuredPersons",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Idnp = table.Column<string>(type: "character varying(13)", maxLength: 13, nullable: false),
                    LastName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    FirstName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Patronymic = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    BirthDate = table.Column<DateOnly>(type: "date", nullable: false),
                    RegisteredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeceased = table.Column<bool>(type: "boolean", nullable: false),
                    DateOfDeath = table.Column<DateOnly>(type: "date", nullable: true),
                    LastRspSyncUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InsuredPersons", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Notifications",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RecipientUserId = table.Column<long>(type: "bigint", nullable: false),
                    Channel = table.Column<int>(type: "integer", nullable: false),
                    Subject = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Body = table.Column<string>(type: "text", nullable: false),
                    DispatchedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReadAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CorrelationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notifications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Reports",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    QueryTemplate = table.Column<string>(type: "text", nullable: false),
                    ParameterSchemaJson = table.Column<string>(type: "jsonb", nullable: false),
                    DefaultFormat = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    IsPublic = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Reports", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ServicePassports",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    NameRo = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    NameEn = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NameRu = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    DescriptionRo = table.Column<string>(type: "text", nullable: false),
                    FormSchemaJson = table.Column<string>(type: "jsonb", nullable: false),
                    WorkflowCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    MaxProcessingDays = table.Column<int>(type: "integer", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    IsProactive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServicePassports", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Solicitants",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    NationalId = table.Column<string>(type: "character varying(13)", maxLength: 13, nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    PhoneE164 = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    PreferredLanguage = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    PostalAddress = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    AffiliatedLegalEntityId = table.Column<string>(type: "character varying(13)", maxLength: 13, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Solicitants", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserProfiles",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MPassSubject = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    LocalLogin = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    LocalPasswordHash = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    NationalId = table.Column<string>(type: "character varying(13)", maxLength: 13, nullable: true),
                    DisplayName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    PreferredLanguage = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Roles = table.Column<List<string>>(type: "text[]", nullable: false),
                    Groups = table.Column<List<string>>(type: "text[]", nullable: false),
                    LastLoginUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsLocked = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserProfiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowTasks",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DossierId = table.Column<long>(type: "bigint", nullable: false),
                    Title = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    AssignedUserId = table.Column<long>(type: "bigint", nullable: true),
                    GroupCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DueAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowTasks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ServiceApplications",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SolicitantId = table.Column<long>(type: "bigint", nullable: false),
                    ServicePassportId = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    FormPayloadJson = table.Column<string>(type: "jsonb", nullable: false),
                    SnapshotJson = table.Column<string>(type: "jsonb", nullable: true),
                    SubmittedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ClosedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DossierId = table.Column<long>(type: "bigint", nullable: true),
                    ReferenceNumber = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceApplications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ServiceApplications_Solicitants_SolicitantId",
                        column: x => x.SolicitantId,
                        principalSchema: "cnas",
                        principalTable: "Solicitants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Dossiers",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ApplicationId = table.Column<long>(type: "bigint", nullable: false),
                    DossierNumber = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AssignedExaminerId = table.Column<long>(type: "bigint", nullable: true),
                    ApproverId = table.Column<long>(type: "bigint", nullable: true),
                    AcceptedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ClosedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dossiers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Dossiers_ServiceApplications_ApplicationId",
                        column: x => x.ApplicationId,
                        principalSchema: "cnas",
                        principalTable: "ServiceApplications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_CreatedAtUtc",
                schema: "cnas",
                table: "AuditLogs",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_EventCode_EventAtUtc",
                schema: "cnas",
                table: "AuditLogs",
                columns: new[] { "EventCode", "EventAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_IsActive",
                schema: "cnas",
                table: "AuditLogs",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_Severity",
                schema: "cnas",
                table: "AuditLogs",
                column: "Severity");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_TargetEntity_TargetEntityId",
                schema: "cnas",
                table: "AuditLogs",
                columns: new[] { "TargetEntity", "TargetEntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_Classifiers_CreatedAtUtc",
                schema: "cnas",
                table: "Classifiers",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Classifiers_IsActive",
                schema: "cnas",
                table: "Classifiers",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_Classifiers_Kind_Code",
                schema: "cnas",
                table: "Classifiers",
                columns: new[] { "Kind", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Classifiers_Kind_ParentCode",
                schema: "cnas",
                table: "Classifiers",
                columns: new[] { "Kind", "ParentCode" });

            migrationBuilder.CreateIndex(
                name: "IX_Contributors_CreatedAtUtc",
                schema: "cnas",
                table: "Contributors",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Contributors_Idno",
                schema: "cnas",
                table: "Contributors",
                column: "Idno",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Contributors_IsActive",
                schema: "cnas",
                table: "Contributors",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_Contributors_IsInsolvent",
                schema: "cnas",
                table: "Contributors",
                column: "IsInsolvent");

            migrationBuilder.CreateIndex(
                name: "IX_Documents_ContentSha256Hex",
                schema: "cnas",
                table: "Documents",
                column: "ContentSha256Hex");

            migrationBuilder.CreateIndex(
                name: "IX_Documents_CreatedAtUtc",
                schema: "cnas",
                table: "Documents",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Documents_DossierId",
                schema: "cnas",
                table: "Documents",
                column: "DossierId");

            migrationBuilder.CreateIndex(
                name: "IX_Documents_IsActive",
                schema: "cnas",
                table: "Documents",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_Dossiers_ApplicationId",
                schema: "cnas",
                table: "Dossiers",
                column: "ApplicationId");

            migrationBuilder.CreateIndex(
                name: "IX_Dossiers_ApproverId",
                schema: "cnas",
                table: "Dossiers",
                column: "ApproverId");

            migrationBuilder.CreateIndex(
                name: "IX_Dossiers_AssignedExaminerId",
                schema: "cnas",
                table: "Dossiers",
                column: "AssignedExaminerId");

            migrationBuilder.CreateIndex(
                name: "IX_Dossiers_CreatedAtUtc",
                schema: "cnas",
                table: "Dossiers",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Dossiers_DossierNumber",
                schema: "cnas",
                table: "Dossiers",
                column: "DossierNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Dossiers_IsActive",
                schema: "cnas",
                table: "Dossiers",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_InsuredPersons_BirthDate",
                schema: "cnas",
                table: "InsuredPersons",
                column: "BirthDate");

            migrationBuilder.CreateIndex(
                name: "IX_InsuredPersons_CreatedAtUtc",
                schema: "cnas",
                table: "InsuredPersons",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_InsuredPersons_Idnp",
                schema: "cnas",
                table: "InsuredPersons",
                column: "Idnp",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InsuredPersons_IsActive",
                schema: "cnas",
                table: "InsuredPersons",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_InsuredPersons_IsDeceased",
                schema: "cnas",
                table: "InsuredPersons",
                column: "IsDeceased");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_CorrelationId",
                schema: "cnas",
                table: "Notifications",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_CreatedAtUtc",
                schema: "cnas",
                table: "Notifications",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_IsActive",
                schema: "cnas",
                table: "Notifications",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_RecipientUserId_ReadAtUtc",
                schema: "cnas",
                table: "Notifications",
                columns: new[] { "RecipientUserId", "ReadAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Reports_Code",
                schema: "cnas",
                table: "Reports",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Reports_CreatedAtUtc",
                schema: "cnas",
                table: "Reports",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Reports_IsActive",
                schema: "cnas",
                table: "Reports",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_Reports_IsPublic",
                schema: "cnas",
                table: "Reports",
                column: "IsPublic");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceApplications_CreatedAtUtc",
                schema: "cnas",
                table: "ServiceApplications",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceApplications_IsActive",
                schema: "cnas",
                table: "ServiceApplications",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceApplications_ReferenceNumber",
                schema: "cnas",
                table: "ServiceApplications",
                column: "ReferenceNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ServiceApplications_SolicitantId",
                schema: "cnas",
                table: "ServiceApplications",
                column: "SolicitantId");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceApplications_Status",
                schema: "cnas",
                table: "ServiceApplications",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceApplications_SubmittedAtUtc",
                schema: "cnas",
                table: "ServiceApplications",
                column: "SubmittedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ServicePassports_Code",
                schema: "cnas",
                table: "ServicePassports",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ServicePassports_CreatedAtUtc",
                schema: "cnas",
                table: "ServicePassports",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ServicePassports_IsActive",
                schema: "cnas",
                table: "ServicePassports",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_ServicePassports_IsEnabled",
                schema: "cnas",
                table: "ServicePassports",
                column: "IsEnabled");

            migrationBuilder.CreateIndex(
                name: "IX_Solicitants_CreatedAtUtc",
                schema: "cnas",
                table: "Solicitants",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Solicitants_IsActive",
                schema: "cnas",
                table: "Solicitants",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_Solicitants_NationalId",
                schema: "cnas",
                table: "Solicitants",
                column: "NationalId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserProfiles_CreatedAtUtc",
                schema: "cnas",
                table: "UserProfiles",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_UserProfiles_IsActive",
                schema: "cnas",
                table: "UserProfiles",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_UserProfiles_LocalLogin",
                schema: "cnas",
                table: "UserProfiles",
                column: "LocalLogin",
                unique: true,
                filter: "\"LocalLogin\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_UserProfiles_MPassSubject",
                schema: "cnas",
                table: "UserProfiles",
                column: "MPassSubject",
                unique: true,
                filter: "\"MPassSubject\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_UserProfiles_NationalId",
                schema: "cnas",
                table: "UserProfiles",
                column: "NationalId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowTasks_AssignedUserId_Status",
                schema: "cnas",
                table: "WorkflowTasks",
                columns: new[] { "AssignedUserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowTasks_CreatedAtUtc",
                schema: "cnas",
                table: "WorkflowTasks",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowTasks_DueAtUtc",
                schema: "cnas",
                table: "WorkflowTasks",
                column: "DueAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowTasks_GroupCode_Status",
                schema: "cnas",
                table: "WorkflowTasks",
                columns: new[] { "GroupCode", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowTasks_IsActive",
                schema: "cnas",
                table: "WorkflowTasks",
                column: "IsActive");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditLogs",
                schema: "cnas");

            migrationBuilder.DropTable(
                name: "Classifiers",
                schema: "cnas");

            migrationBuilder.DropTable(
                name: "Contributors",
                schema: "cnas");

            migrationBuilder.DropTable(
                name: "Documents",
                schema: "cnas");

            migrationBuilder.DropTable(
                name: "Dossiers",
                schema: "cnas");

            migrationBuilder.DropTable(
                name: "InsuredPersons",
                schema: "cnas");

            migrationBuilder.DropTable(
                name: "Notifications",
                schema: "cnas");

            migrationBuilder.DropTable(
                name: "Reports",
                schema: "cnas");

            migrationBuilder.DropTable(
                name: "ServicePassports",
                schema: "cnas");

            migrationBuilder.DropTable(
                name: "UserProfiles",
                schema: "cnas");

            migrationBuilder.DropTable(
                name: "WorkflowTasks",
                schema: "cnas");

            migrationBuilder.DropTable(
                name: "ServiceApplications",
                schema: "cnas");

            migrationBuilder.DropTable(
                name: "Solicitants",
                schema: "cnas");
        }
    }
}
