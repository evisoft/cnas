using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// R0171 / CF 22.02 / CF 04.08 — adds the <c>UserProfiles.NotificationPreferences</c>
    /// JSONB column that carries each user's per-channel opt-in flags
    /// (Email / SMS / InApp) plus the reserved <c>categories</c> sub-object for the
    /// follow-up per-workflow strategy (R0173).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Nullable column, no back-fill.</b> The column is added nullable so existing rows
    /// materialise with <c>NULL</c>. The dispatcher
    /// (<c>NotificationService.EnqueueAsync</c>) parses NULL as
    /// <c>NotificationPreferences.Default</c> (every channel opted IN), so pre-migration
    /// users continue to receive notifications unchanged until they explicitly opt out
    /// via <c>PUT /api/profile/notification-preferences</c>. No DML is required at
    /// migration time.
    /// </para>
    /// <para>
    /// <b>JSONB rationale.</b> The column carries only boolean flags plus a small
    /// dictionary; JSONB gives compact storage and future indexability without paying
    /// the per-document parse cost of <c>jsonpath</c> queries on plain TEXT. The
    /// InMemory provider used in tests treats it as a regular nullable string column —
    /// see <c>UserProfileConfiguration</c> for the EF wiring.
    /// </para>
    /// <para>
    /// <b>No PII drift.</b> The JSON contains only booleans and category code strings.
    /// It does NOT participate in the encrypted-at-rest set (CLAUDE.md §5.7 /
    /// TOR SEC 035).
    /// </para>
    /// </remarks>
    public partial class AddUserNotificationPreferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.AddColumn<string>(
                name: "NotificationPreferences",
                schema: "cnas",
                table: "UserProfiles",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropColumn(
                name: "NotificationPreferences",
                schema: "cnas",
                table: "UserProfiles");
        }
    }
}
