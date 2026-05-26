using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R0810 / R0811 / R0812 — maps <see cref="Declaration"/> to <c>cnas.Declarations</c>.
/// Two indexes back the natural-key uniqueness rule documented on the entity:
/// <list type="bullet">
///   <item>A filtered unique index on
///     <c>(ContributorId, Kind, ReportingMonth, ReferenceNumber) WHERE ReferenceNumber
///     IS NOT NULL</c> enforces "the same external reference cannot be re-registered
///     for the same payer / kind / month".</item>
///   <item>A non-unique index on <c>(ContributorId, Kind, ReportingMonth)</c> when
///     <c>ReferenceNumber IS NULL</c> — multiple anonymous rows are permitted because
///     control / court adjustments often lack a reference number.</item>
/// </list>
/// Two listing-style indexes back the R0813 aggregator and the per-payer history
/// listing.
/// </summary>
public sealed class DeclarationConfiguration : AuditableEntityConfiguration<Declaration>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<Declaration> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Declarations");

        builder.Property(e => e.ContributorId).IsRequired();
        builder.Property(e => e.Kind).IsRequired().HasConversion<int>();
        builder.Property(e => e.ReportingMonth).IsRequired();
        builder.Property(e => e.FiledAtUtc).IsRequired();
        builder.Property(e => e.ReferenceNumber).HasMaxLength(64);
        builder.Property(e => e.DeclaredContributionAmount).HasPrecision(18, 2);
        builder.Property(e => e.AdjustedContributionAmount).HasPrecision(18, 2);
        builder.Property(e => e.Status).IsRequired().HasConversion<int>();
        builder.Property(e => e.Notes).HasMaxLength(500);
        builder.Property(e => e.IsArchived).IsRequired().HasDefaultValue(false);

        // R0821 / R0823 / Annex 1 §8.1.3 — scanned-copy + OCR metadata columns.
        // OcrExtractedJson capped at 100_000 chars by the input validator; the
        // schema column itself is unbounded text (a citizen's scanned form may
        // legitimately exceed the FluentValidation cap once OCR fidelity
        // improves and we relax the validator without a schema change).
        builder.Property(e => e.OcrExtractedJson);
        builder.Property(e => e.OcrConfidenceLevel).HasMaxLength(16);
        builder.Property(e => e.HasScannedCopy).IsRequired().HasDefaultValue(false);
        builder.Property(e => e.RegisteredByOffice).HasMaxLength(32);
        builder.Property(e => e.FormVersion).HasMaxLength(32);

        // Filtered unique index — applies only when ReferenceNumber is non-null.
        // Same payer / kind / month / reference cannot be registered twice; rows
        // without a reference are not covered by the constraint.
        builder.HasIndex(e => new { e.ContributorId, e.Kind, e.ReportingMonth, e.ReferenceNumber })
            .HasFilter("\"ReferenceNumber\" IS NOT NULL")
            .IsUnique()
            .HasDatabaseName("UX_Declarations_NaturalKey");

        // Listing index — the R0813 aggregator and per-payer history filter by
        // (ContributorId, ReportingMonth DESC). The composite covers both shapes.
        builder.HasIndex(e => new { e.ContributorId, e.ReportingMonth })
            .HasDatabaseName("IX_Declarations_Contributor_Month");

        // Kind-month index — operator queries that filter by declaration family
        // (e.g. "all SFS rows for May 2026").
        builder.HasIndex(e => new { e.Kind, e.ReportingMonth })
            .HasDatabaseName("IX_Declarations_Kind_Month");

        // R0822 — explorer index. The Declarations registry-explorer endpoint
        // commonly filters by "rows that carry a scanned copy"; the single-
        // column index keeps the read path cheap without paying for a wider
        // composite index that the explorer does not need.
        builder.HasIndex(e => e.HasScannedCopy)
            .HasDatabaseName("IX_Declarations_HasScannedCopy");
    }
}
