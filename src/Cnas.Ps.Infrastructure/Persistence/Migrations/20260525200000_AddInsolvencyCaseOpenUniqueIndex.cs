using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// iter-149 / R0830 — placeholder migration for the partial unique index
    /// <c>UX_InsolvencyCases_Open_Per_Contributor</c> that enforces "at most one
    /// open insolvency case per contributor" at the database layer. The real
    /// index definition lives on
    /// <see cref="Cnas.Ps.Infrastructure.Persistence.Configurations.InsolvencyCaseConfiguration"/>
    /// (declared with <c>HasFilter("\"Status\" = 0")</c>) and is materialised by
    /// EF Core via the <c>Status = Open</c> integer-literal predicate when the
    /// Postgres provider is in use. The InMemory provider used by the existing
    /// integration tests is silent on partial-index enforcement, so the TOCTOU
    /// defence remains the pre-check + DbUpdateException catch in
    /// <see cref="Cnas.Ps.Infrastructure.Services.InsolvencyLifecycleService.OpenAsync"/>;
    /// against Postgres the index makes the second concurrent insert fail at the
    /// constraint layer, and the catch translates that to the same stable
    /// <c>Conflict</c> error code the pre-check returns.
    /// </summary>
    /// <remarks>
    /// Deferred-by-design — mirrors the pattern on the other iter-148/149
    /// placeholder migrations (<see cref="AddRecurrentPaymentSchedules"/>,
    /// <see cref="AddVoucherQuotas"/>, <see cref="AddInsolvencyTables"/>). The
    /// production migration body is filled in alongside the first concrete
    /// Postgres roll-out — the runtime model snapshot is what drives the
    /// InMemory schema; the partial index becomes load-bearing only on the
    /// relational provider.
    /// </remarks>
    public partial class AddInsolvencyCaseOpenUniqueIndex : Migration
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
