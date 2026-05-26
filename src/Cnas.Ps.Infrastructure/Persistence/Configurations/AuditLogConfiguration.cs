using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>Maps <see cref="AuditLog"/> to <c>cnas.AuditLogs</c>.</summary>
public sealed class AuditLogConfiguration : AuditableEntityConfiguration<AuditLog>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<AuditLog> builder)
    {
        builder.ToTable("AuditLogs");

        builder.Property(a => a.Severity).IsRequired().HasConversion<int>();
        builder.Property(a => a.EventCode).IsRequired().HasMaxLength(128);
        builder.Property(a => a.ActorId).IsRequired().HasMaxLength(128);
        builder.Property(a => a.TargetEntity).HasMaxLength(64);
        builder.Property(a => a.SourceIp).HasMaxLength(64);
        builder.Property(a => a.CorrelationId).HasMaxLength(64);
        builder.Property(a => a.DetailsJson).HasColumnType("jsonb");

        // R0194 / SEC 047 — hash-chain columns. SHA-256 hex is 64 chars; the
        // genesis literal "GENESIS" is 7 — both fit comfortably in the same
        // bounded column. The cap is mostly index-efficiency; without it the
        // column would default to text and the secondary index would be wider
        // than necessary.
        builder.Property(a => a.PrevHash).IsRequired().HasMaxLength(64);
        builder.Property(a => a.RowHash).IsRequired().HasMaxLength(64);
        builder.HasIndex(a => a.RowHash);

        builder.HasIndex(a => new { a.EventCode, a.EventAtUtc });
        builder.HasIndex(a => new { a.TargetEntity, a.TargetEntityId });
        builder.HasIndex(a => a.Severity);
    }
}
