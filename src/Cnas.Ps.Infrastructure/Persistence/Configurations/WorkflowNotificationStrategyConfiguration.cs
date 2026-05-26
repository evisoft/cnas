using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="WorkflowNotificationStrategy"/> to <c>cnas.WorkflowNotificationStrategies</c>
/// — the per-workflow notification-strategy registry consulted by the workflow
/// orchestrator on every lifecycle event (R0128 / R0173 / CF 16.14 / CF 22.04).
/// </summary>
/// <remarks>
/// <para>
/// <b>Natural key.</b> Composite UNIQUE on (<see cref="WorkflowNotificationStrategy.WorkflowDefinitionId"/>,
/// <see cref="WorkflowNotificationStrategy.EventCode"/>) — one strategy per (workflow,
/// event). The CRUD service's <c>UpsertAsync</c> picks the existing row off this index;
/// the database-level uniqueness is the safety net.
/// </para>
/// <para>
/// <b>Channels / RecipientRoles serialisation.</b> Stored as <c>jsonb</c> with a value
/// converter (mirrors <see cref="AuditPolicy.ExtraRedactKeys"/>). The
/// <see cref="ValueComparer{T}"/> compares element-by-element ordinally so EF Core
/// tracks list mutations correctly across the round trip and idempotent updates
/// (assigning the same list) avoid spurious <c>DbUpdateConcurrencyException</c> on the
/// xmin token.
/// </para>
/// <para>
/// <b>Column caps.</b> 64-char <see cref="WorkflowNotificationStrategy.EventCode"/>
/// (every canonical event code stays well below this), 64-char
/// <see cref="WorkflowNotificationStrategy.TemplateCodeOverride"/>, 512-char
/// <see cref="WorkflowNotificationStrategy.Description"/>.
/// </para>
/// </remarks>
public sealed class WorkflowNotificationStrategyConfiguration : AuditableEntityConfiguration<WorkflowNotificationStrategy>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<WorkflowNotificationStrategy> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("WorkflowNotificationStrategies");

        builder.Property(s => s.WorkflowDefinitionId).IsRequired();
        builder.Property(s => s.EventCode).IsRequired().HasMaxLength(64);
        builder.Property(s => s.IsEnabled).IsRequired().HasDefaultValue(true);
        builder.Property(s => s.TemplateCodeOverride).HasMaxLength(64);
        builder.Property(s => s.QuietHoursStartLocalMinute);
        builder.Property(s => s.QuietHoursEndLocalMinute);
        builder.Property(s => s.Description).HasMaxLength(512);

        // Channels persist as JSONB. The value comparer keeps EF change tracking
        // accurate across the round trip (List<TEnum> reference equality alone would
        // otherwise force spurious xmin concurrency conflicts on idempotent updates).
        builder.Property(s => s.Channels)
            .HasColumnType("jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(
                    (v ?? new List<NotificationChannel>()).Select(c => c.ToString()).ToList(),
                    (JsonSerializerOptions?)null),
                v => string.IsNullOrEmpty(v)
                    ? new List<NotificationChannel>()
                    : (JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null) ?? new List<string>())
                        .Select(s => Enum.Parse<NotificationChannel>(s, ignoreCase: false))
                        .ToList(),
                new ValueComparer<List<NotificationChannel>>(
                    (a, b) => (a ?? new List<NotificationChannel>()).SequenceEqual(b ?? new List<NotificationChannel>()),
                    v => (v ?? new List<NotificationChannel>()).Aggregate(0, (acc, c) => HashCode.Combine(acc, (int)c)),
                    v => v.ToList()));

        // RecipientRoles persist as JSONB list of strings.
        builder.Property(s => s.RecipientRoles)
            .HasColumnType("jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v ?? new List<string>(), (JsonSerializerOptions?)null),
                v => string.IsNullOrEmpty(v)
                    ? new List<string>()
                    : JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null) ?? new List<string>(),
                new ValueComparer<List<string>>(
                    (a, b) => (a ?? new List<string>()).SequenceEqual(b ?? new List<string>(), StringComparer.Ordinal),
                    v => (v ?? new List<string>()).Aggregate(0, (acc, s) => HashCode.Combine(acc, s.GetHashCode(StringComparison.Ordinal))),
                    v => v.ToList()));

        // Composite natural-key UNIQUE: one strategy per (workflow, event).
        builder.HasIndex(s => new { s.WorkflowDefinitionId, s.EventCode }).IsUnique();
    }
}
