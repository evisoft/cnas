using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Adds the <c>DeliveryStatus</c> column to <c>cnas.Notifications</c> — the authoritative
    /// signal for the Annex 6g <c>RPT-NOTIFICATIONS-DELIVERY</c> report. The column is a
    /// non-nullable <c>integer</c> with a server-side default of <c>0</c>
    /// (<see cref="Cnas.Ps.Core.Domain.NotificationDeliveryStatus.Pending"/>), and a
    /// non-clustered index supports the per-channel GROUP BY in the report builder.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Back-fill semantics for existing rows (executed by the raw SQL in <see cref="Up"/>):
    /// <list type="bullet">
    ///   <item><description><c>DispatchedAtUtc IS NOT NULL</c> ⇒ <c>DeliveryStatus = 1</c> (Delivered).</description></item>
    ///   <item><description><c>DispatchedAtUtc IS NULL</c> ⇒ <c>DeliveryStatus = 0</c> (Pending) — the column default already covers this; we deliberately do NOT back-fill these as Failed because the old heuristic that conflated them was incorrect.</description></item>
    /// </list>
    /// The EF InMemory provider used by the unit tests skips raw SQL harmlessly; Postgres
    /// applies it transactionally.
    /// </para>
    /// <para>
    /// Down migration drops the index and the column. Existing rows that had their delivery
    /// outcome back-filled from <c>DispatchedAtUtc</c> survive the rollback intact — only the
    /// new column is removed.
    /// </para>
    /// </remarks>
    public partial class AddNotificationDeliveryStatus : Migration
    {
        /// <summary>Adds the <c>DeliveryStatus</c> column, the supporting index, and back-fills delivered rows from <c>DispatchedAtUtc</c>.</summary>
        /// <param name="migrationBuilder">EF Core migration builder.</param>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DeliveryStatus",
                schema: "cnas",
                table: "Notifications",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_DeliveryStatus",
                schema: "cnas",
                table: "Notifications",
                column: "DeliveryStatus");

            // Back-fill: any existing row that has been dispatched in the past is conceptually
            // "Delivered" (= 1). Rows with a null DispatchedAtUtc stay at the column default
            // value 0 (Pending) — we cannot retroactively claim they failed.
            // InMemory providers ignore raw SQL safely; PostgreSQL applies it transactionally.
            migrationBuilder.Sql(
                "UPDATE cnas.\"Notifications\" SET \"DeliveryStatus\" = 1 WHERE \"DispatchedAtUtc\" IS NOT NULL;");
        }

        /// <summary>Drops the index and the <c>DeliveryStatus</c> column.</summary>
        /// <param name="migrationBuilder">EF Core migration builder.</param>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Notifications_DeliveryStatus",
                schema: "cnas",
                table: "Notifications");

            migrationBuilder.DropColumn(
                name: "DeliveryStatus",
                schema: "cnas",
                table: "Notifications");
        }
    }
}
