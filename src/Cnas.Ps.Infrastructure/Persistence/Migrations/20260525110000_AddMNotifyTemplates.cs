using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cnas.Ps.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// R0115 / TOR CF 14.07 — placeholder migration for the additive
    /// <c>cnas.MNotifyTemplates</c> table introduced in iter-140.
    /// </summary>
    /// <remarks>
    /// Deferred-by-design — see the matching pattern on
    /// <see cref="AddDecisionSupersessions"/>.
    /// </remarks>
    public partial class AddMNotifyTemplates : Migration
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
