using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R0127 / CF 16.11 — EF Core mapping for <see cref="UserAbsence"/>. Persists the
/// absence row to <c>cnas.UserAbsences</c>. Two indexes:
/// <list type="bullet">
///   <item><c>(UserUserId, Status)</c> — backs the "list absences for user" query and
///   the "find planned/active for user" overlap check at planning time.</item>
///   <item><c>(StartDateUtc, EndDateUtc, Status)</c> — backs the lifecycle-job sweep
///   that flips planned rows to active and active rows to completed.</item>
/// </list>
/// </summary>
public sealed class UserAbsenceConfiguration : AuditableEntityConfiguration<UserAbsence>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<UserAbsence> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("UserAbsences");

        builder.Property(a => a.Reason).IsRequired().HasMaxLength(200);
        builder.Property(a => a.Status).IsRequired().HasConversion<int>();
        builder.Property(a => a.StartDateUtc).IsRequired();
        builder.Property(a => a.EndDateUtc).IsRequired();
        builder.Property(a => a.RoutedTaskCount).IsRequired().HasDefaultValue(0);

        // Backs "list absences for this user" and the overlap check at planning time.
        builder.HasIndex(a => new { a.UserUserId, a.Status });

        // Backs the lifecycle-job sweep (Planned past StartDateUtc → Activate; Active
        // past EndDateUtc → Complete). Status filters down to a tiny working set.
        builder.HasIndex(a => new { a.StartDateUtc, a.EndDateUtc, a.Status });
    }
}
