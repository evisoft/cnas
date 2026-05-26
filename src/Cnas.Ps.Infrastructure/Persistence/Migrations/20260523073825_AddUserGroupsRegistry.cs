using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUserGroupsRegistry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UserGroups",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Roles = table.Column<List<string>>(type: "text[]", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserGroups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserGroupMemberships",
                schema: "cnas",
                columns: table => new
                {
                    UserGroupId = table.Column<long>(type: "bigint", nullable: false),
                    UserProfileId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserGroupMemberships", x => new { x.UserGroupId, x.UserProfileId });
                    table.ForeignKey(
                        name: "FK_UserGroupMemberships_UserGroups_UserGroupId",
                        column: x => x.UserGroupId,
                        principalSchema: "cnas",
                        principalTable: "UserGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserGroupMemberships_UserProfiles_UserProfileId",
                        column: x => x.UserProfileId,
                        principalSchema: "cnas",
                        principalTable: "UserProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "UserGroupParents",
                schema: "cnas",
                columns: table => new
                {
                    ParentGroupId = table.Column<long>(type: "bigint", nullable: false),
                    ChildGroupId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserGroupParents", x => new { x.ParentGroupId, x.ChildGroupId });
                    table.CheckConstraint("CK_UserGroupParents_NoSelfLoop", "\"ParentGroupId\" <> \"ChildGroupId\"");
                    table.ForeignKey(
                        name: "FK_UserGroupParents_UserGroups_ChildGroupId",
                        column: x => x.ChildGroupId,
                        principalSchema: "cnas",
                        principalTable: "UserGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UserGroupParents_UserGroups_ParentGroupId",
                        column: x => x.ParentGroupId,
                        principalSchema: "cnas",
                        principalTable: "UserGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserGroupMemberships_UserProfileId",
                schema: "cnas",
                table: "UserGroupMemberships",
                column: "UserProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_UserGroupParents_ChildGroupId",
                schema: "cnas",
                table: "UserGroupParents",
                column: "ChildGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_UserGroups_CreatedAtUtc",
                schema: "cnas",
                table: "UserGroups",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_UserGroups_IsActive",
                schema: "cnas",
                table: "UserGroups",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_UserGroups_Status_Kind",
                schema: "cnas",
                table: "UserGroups",
                columns: new[] { "Status", "Kind" });

            migrationBuilder.CreateIndex(
                name: "UX_UserGroups_Code",
                schema: "cnas",
                table: "UserGroups",
                column: "Code",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserGroupMemberships",
                schema: "cnas");

            migrationBuilder.DropTable(
                name: "UserGroupParents",
                schema: "cnas");

            migrationBuilder.DropTable(
                name: "UserGroups",
                schema: "cnas");
        }
    }
}
