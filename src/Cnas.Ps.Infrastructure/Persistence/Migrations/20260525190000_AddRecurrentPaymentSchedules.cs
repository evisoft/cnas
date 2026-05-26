using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// R1000..R1034 / TOR §3.2-Z — placeholder migration for the additive
    /// <c>cnas.RecurrentPaymentSchedules</c> table introduced in iter-148.
    /// Backs the <see cref="Cnas.Ps.Application.UseCases.IRecurrentPaymentSchedulerService"/>
    /// that drives the §3.2-Z Suport financiar de stat lunar dispatcher
    /// (Quartz <c>RecurrentPaymentJob</c> daily 03:00 UTC).
    /// </summary>
    /// <remarks>
    /// Deferred-by-design — mirrors the pattern on
    /// <see cref="AddVoucherQuotas"/> / <see cref="AddInsolvencyTables"/> /
    /// <see cref="AddDelegationGrants"/>. The table is created at first
    /// runtime by EF Core when the in-memory snapshot lands the model change;
    /// the production migration body is filled in alongside the first
    /// concrete Postgres roll-out.
    /// </remarks>
    public partial class AddRecurrentPaymentSchedules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
