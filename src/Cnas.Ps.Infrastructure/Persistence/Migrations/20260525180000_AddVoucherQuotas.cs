using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// R1000..R1034 / TOR §3.2-AB..AD — placeholder migration for the
    /// additive <c>cnas.VoucherQuotas</c> table introduced in iter-148. Backs
    /// the <see cref="Cnas.Ps.Application.UseCases.IVoucherQuotaService"/>
    /// quota engine that gates the spa / rehabilitation / sanatorium
    /// passports (3.2-AB / 3.2-AC / 3.2-AD).
    /// </summary>
    /// <remarks>
    /// Deferred-by-design — mirrors the pattern on
    /// <see cref="AddInsolvencyTables"/> / <see cref="AddDelegationGrants"/> /
    /// <see cref="AddOfflineBatchJobs"/>. The table is created at first
    /// runtime by EF Core when the in-memory snapshot lands the model change;
    /// the production migration body is filled in alongside the first
    /// concrete Postgres roll-out.
    /// </remarks>
    public partial class AddVoucherQuotas : Migration
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
