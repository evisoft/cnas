using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Creates the <c>cnas.FailedJobs</c> dead-letter-queue table that backs
    /// <see cref="Cnas.Ps.Core.Domain.FailedJob"/>. Per CLAUDE.md §6.2, every Quartz job
    /// must be monitored, retryable, and logged — the three production jobs
    /// (DossierSlaMonitor, MPayDispatcher, MConnectSync) already retry inside their MGov
    /// Polly pipelines (#74); this table captures any failure that survives those
    /// retries so operators can triage and replay without scraping Serilog files.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Schema highlights:
    /// </para>
    /// <list type="bullet">
    ///   <item><description><c>JobName</c> + <c>FailedAtUtc</c> composite index — supports the dashboard query "last failures of job X".</description></item>
    ///   <item><description>Standalone <c>FailedAtUtc</c> index — supports "last N failures across all jobs".</description></item>
    ///   <item><description><c>ExceptionMessage</c> capped at 4 000 chars; <c>StackTrace</c> + <c>JobDataJson</c> use <c>text</c> (already truncated at the application layer).</description></item>
    /// </list>
    /// <para>
    /// Down migration drops the table cleanly — there are no foreign keys into or out of
    /// this row because the DLQ is intentionally decoupled from the rest of the domain
    /// (a deleted job key still leaves an inspectable failure record).
    /// </para>
    /// </remarks>
    public partial class AddFailedJobsTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FailedJobs",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    JobName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    JobGroup = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FailedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExceptionType = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ExceptionMessage = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    StackTrace = table.Column<string>(type: "text", nullable: true),
                    JobDataJson = table.Column<string>(type: "text", nullable: true),
                    RefireCount = table.Column<int>(type: "integer", nullable: false),
                    ReplayState = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    LastReplayAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FailedJobs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FailedJobs_CreatedAtUtc",
                schema: "cnas",
                table: "FailedJobs",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_FailedJobs_FailedAtUtc",
                schema: "cnas",
                table: "FailedJobs",
                column: "FailedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_FailedJobs_IsActive",
                schema: "cnas",
                table: "FailedJobs",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_FailedJobs_JobName_FailedAtUtc",
                schema: "cnas",
                table: "FailedJobs",
                columns: new[] { "JobName", "FailedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FailedJobs",
                schema: "cnas");
        }
    }
}
