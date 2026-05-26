using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>Maps <see cref="ServiceApplication"/> to <c>cnas.ServiceApplications</c>.</summary>
public sealed class ServiceApplicationConfiguration : AuditableEntityConfiguration<ServiceApplication>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<ServiceApplication> builder)
    {
        builder.ToTable("ServiceApplications");

        builder.Property(a => a.SolicitantId).IsRequired();
        builder.Property(a => a.ServicePassportId).IsRequired();
        builder.Property(a => a.Status).IsRequired().HasConversion<int>();
        builder.Property(a => a.FormPayloadJson).IsRequired().HasColumnType("jsonb");
        builder.Property(a => a.SnapshotJson).HasColumnType("jsonb");
        builder.Property(a => a.ReferenceNumber).HasMaxLength(64);
        builder.Property(a => a.PaymentTransactionId).HasMaxLength(64);
        builder.Property(a => a.PaymentStatus).HasMaxLength(32);
        builder.HasIndex(a => a.PaymentDispatchedAtUtc);

        // R0129 / R0142 / CF 15.04 — denormalised snapshots of the workflow + passport
        // version the application was submitted under. Default 1 keeps existing rows
        // (back-filled by migration) round-trippable.
        builder.Property(a => a.PinnedServicePassportVersion).IsRequired().HasDefaultValue(1);
        builder.Property(a => a.PinnedWorkflowVersion).IsRequired().HasDefaultValue(1);

        builder.HasOne(a => a.Solicitant)
            .WithMany()
            .HasForeignKey(a => a.SolicitantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(a => a.Status);
        builder.HasIndex(a => a.SubmittedAtUtc);
        builder.HasIndex(a => a.ReferenceNumber).IsUnique();

        // R0934 — drives the missing-documents SLA filter
        // (Status == RejectedIncomplete AND RejectedIncompleteSinceUtc <= deadline).
        // The job scans the entire table every hour; a non-clustered index keeps
        // the predicate sargable even as the application volume grows.
        builder.HasIndex(a => a.RejectedIncompleteSinceUtc);

        // R0671 / CF 18.06 — subdivision code drives the IAccessScope filter so a
        // staff user sees only applications routed to their CNAS branch. 64 chars
        // matches CnasBranch.Code; index is a plain B-tree for equality / IN match.
        builder.Property(a => a.SubdivisionCode).HasMaxLength(64);
        builder.HasIndex(a => a.SubdivisionCode);

        // R0570 / TOR CF 08.02 — examiner assigned at submission time via the
        // round-robin distribution service. Indexed so the per-examiner queue
        // ("show me the cereri assigned to me but not yet picked into a dossier")
        // remains a single index seek instead of a sequential scan.
        builder.HasIndex(a => a.AssignedExaminerUserId);
    }
}
