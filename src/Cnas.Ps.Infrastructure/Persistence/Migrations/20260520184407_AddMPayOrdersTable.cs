using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Creates the <c>cnas.MPayOrders</c> table that backs
    /// <see cref="Cnas.Ps.Core.Domain.MPayOrder"/>. Persists one row per outbound MPay
    /// payment order so the inbound MPay callbacks
    /// (<c>GET /api/mpay/orders/{orderId}/details</c>,
    /// <c>POST /api/mpay/orders/{orderId}/confirm</c>) become idempotent — the row is
    /// the natural-key anchor for retried confirmations (CLAUDE.md cross-cutting
    /// "Idempotent Callbacks", red-flag #15).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two domain-specific indexes are created:
    /// <list type="bullet">
    ///   <item><description><c>UNIQUE (OrderId)</c> — natural key.</description></item>
    ///   <item><description><c>(OrderId, PaymentRef)</c> — composite lookup index supporting the idempotency guard in <c>MPayOrderStore.ConfirmAsync</c>.</description></item>
    /// </list>
    /// The standard <c>(IsActive)</c> and <c>(CreatedAtUtc)</c> indexes inherited from
    /// <see cref="Cnas.Ps.Infrastructure.Persistence.Configurations.AuditableEntityConfiguration{TEntity}"/>
    /// are also created.
    /// </para>
    /// <para>
    /// Down migration drops the table cleanly — no foreign keys cross into or out of
    /// this row (the link to the rest of the schema is the opaque business identifier
    /// <c>OrderId</c>, not a surrogate key).
    /// </para>
    /// </remarks>
    public partial class AddMPayOrdersTable : Migration
    {
        /// <summary>Creates <c>cnas.MPayOrders</c> with its four indexes (natural-key unique, composite confirm-lookup, soft-delete, audit-timestamp).</summary>
        /// <param name="migrationBuilder">EF Core migration builder.</param>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MPayOrders",
                schema: "cnas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OrderId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AmountMdl = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    DescriptionRo = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    BeneficiaryIdnp = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    PaymentRef = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ConfirmedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MPayOrders", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MPayOrders_CreatedAtUtc",
                schema: "cnas",
                table: "MPayOrders",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_MPayOrders_IsActive",
                schema: "cnas",
                table: "MPayOrders",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_MPayOrders_OrderId",
                schema: "cnas",
                table: "MPayOrders",
                column: "OrderId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MPayOrders_OrderId_PaymentRef",
                schema: "cnas",
                table: "MPayOrders",
                columns: new[] { "OrderId", "PaymentRef" });
        }

        /// <summary>Drops <c>cnas.MPayOrders</c> and all its indexes.</summary>
        /// <param name="migrationBuilder">EF Core migration builder.</param>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MPayOrders",
                schema: "cnas");
        }
    }
}
