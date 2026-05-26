using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddClassificationCatalogRegistry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClassificationCatalogSnapshots",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CapturedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TriggerKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    TotalTypesScanned = table.Column<int>(type: "integer", nullable: false),
                    TotalPropertiesClassified = table.Column<int>(type: "integer", nullable: false),
                    TotalPropertiesUnclassified = table.Column<int>(type: "integer", nullable: false),
                    LabelCountsJson = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    AssemblyVersionsJson = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    FailureReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClassificationCatalogSnapshots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ClassificationCatalogEntries",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SnapshotId = table.Column<long>(type: "bigint", nullable: false),
                    TypeFullName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    PropertyName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Label = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    IsExplicit = table.Column<bool>(type: "boolean", nullable: false),
                    DeclaringAssembly = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClassificationCatalogEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClassificationCatalogEntries_ClassificationCatalogSnapshots~",
                        column: x => x.SnapshotId,
                        principalSchema: "cnas",
                        principalTable: "ClassificationCatalogSnapshots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ClassificationDriftFindings",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BaselineSnapshotId = table.Column<long>(type: "bigint", nullable: false),
                    CurrentSnapshotId = table.Column<long>(type: "bigint", nullable: false),
                    DriftKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    TypeFullName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    PropertyName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    BaselineLabel = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    CurrentLabel = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Acknowledged = table.Column<bool>(type: "boolean", nullable: false),
                    AcknowledgedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    AcknowledgedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AcknowledgementNote = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    DetectedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClassificationDriftFindings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClassificationDriftFindings_ClassificationCatalogSnapshots_~",
                        column: x => x.BaselineSnapshotId,
                        principalSchema: "cnas",
                        principalTable: "ClassificationCatalogSnapshots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ClassificationDriftFindings_ClassificationCatalogSnapshots~1",
                        column: x => x.CurrentSnapshotId,
                        principalSchema: "cnas",
                        principalTable: "ClassificationCatalogSnapshots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClassificationCatalogEntries_CreatedAtUtc",
                schema: "cnas",
                table: "ClassificationCatalogEntries",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ClassificationCatalogEntries_IsActive",
                schema: "cnas",
                table: "ClassificationCatalogEntries",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_ClassificationCatalogEntries_IsExplicit",
                schema: "cnas",
                table: "ClassificationCatalogEntries",
                column: "IsExplicit");

            migrationBuilder.CreateIndex(
                name: "IX_ClassificationCatalogEntries_Label",
                schema: "cnas",
                table: "ClassificationCatalogEntries",
                column: "Label");

            migrationBuilder.CreateIndex(
                name: "UX_ClassificationCatalogEntries_Snapshot_Type_Property",
                schema: "cnas",
                table: "ClassificationCatalogEntries",
                columns: new[] { "SnapshotId", "TypeFullName", "PropertyName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClassificationCatalogSnapshots_CapturedAt",
                schema: "cnas",
                table: "ClassificationCatalogSnapshots",
                column: "CapturedAt",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_ClassificationCatalogSnapshots_CreatedAtUtc",
                schema: "cnas",
                table: "ClassificationCatalogSnapshots",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ClassificationCatalogSnapshots_IsActive",
                schema: "cnas",
                table: "ClassificationCatalogSnapshots",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_ClassificationCatalogSnapshots_Status",
                schema: "cnas",
                table: "ClassificationCatalogSnapshots",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_ClassificationDriftFindings_Acknowledged_DetectedAt",
                schema: "cnas",
                table: "ClassificationDriftFindings",
                columns: new[] { "Acknowledged", "DetectedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ClassificationDriftFindings_BaselineId_CurrentId",
                schema: "cnas",
                table: "ClassificationDriftFindings",
                columns: new[] { "BaselineSnapshotId", "CurrentSnapshotId" });

            migrationBuilder.CreateIndex(
                name: "IX_ClassificationDriftFindings_CreatedAtUtc",
                schema: "cnas",
                table: "ClassificationDriftFindings",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ClassificationDriftFindings_CurrentSnapshotId",
                schema: "cnas",
                table: "ClassificationDriftFindings",
                column: "CurrentSnapshotId");

            migrationBuilder.CreateIndex(
                name: "IX_ClassificationDriftFindings_DriftKind",
                schema: "cnas",
                table: "ClassificationDriftFindings",
                column: "DriftKind");

            migrationBuilder.CreateIndex(
                name: "IX_ClassificationDriftFindings_IsActive",
                schema: "cnas",
                table: "ClassificationDriftFindings",
                column: "IsActive");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClassificationCatalogEntries",
                schema: "cnas");

            migrationBuilder.DropTable(
                name: "ClassificationDriftFindings",
                schema: "cnas");

            migrationBuilder.DropTable(
                name: "ClassificationCatalogSnapshots",
                schema: "cnas");
        }
    }
}
