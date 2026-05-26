using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="ServicePassport"/> to <c>cnas.ServicePassports</c>.
/// </summary>
/// <remarks>
/// R0142 / CF 15.04 — the table is append-only (one row per <c>(Code, Version)</c>) and
/// the natural-key uniqueness moves from the old global <c>(Code)</c> unique index to two
/// new indexes: <c>(Code, Version) UNIQUE</c> as the natural-key safety net, plus a
/// partial unique index on <c>(Code) WHERE IsCurrent = true</c> that enforces the
/// invariant "at most one current row per code" at the database layer.
/// </remarks>
public sealed class ServicePassportConfiguration : AuditableEntityConfiguration<ServicePassport>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<ServicePassport> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ServicePassports");

        builder.Property(s => s.Code).IsRequired().HasMaxLength(64);
        builder.Property(s => s.Category).HasMaxLength(64);
        builder.Property(s => s.NameRo).IsRequired().HasMaxLength(256);
        builder.Property(s => s.NameEn).HasMaxLength(256);
        builder.Property(s => s.NameRu).HasMaxLength(256);
        builder.Property(s => s.DescriptionRo).IsRequired();
        builder.Property(s => s.FormSchemaJson).HasColumnType("jsonb");
        builder.Property(s => s.WorkflowCode).IsRequired().HasMaxLength(64);
        builder.Property(s => s.DecisionRulesJson)
            .HasColumnType("text")
            .IsRequired()
            .HasDefaultValue("{}");

        // R0143 / CF 17.19 — optional JSON columns carrying the per-passport mandatory-
        // attachments matrix + named calc-formula expressions. Both nullable so legacy
        // passport rows continue to work unchanged; both stored as jsonb for future
        // server-side introspection (e.g. coverage reports).
        builder.Property(s => s.MandatoryAttachmentsJson).HasColumnType("jsonb");
        builder.Property(s => s.CalcFormulasJson).HasColumnType("jsonb");

        // R0142 / CF 15.04 — versioning columns.
        builder.Property(s => s.Version).IsRequired().HasDefaultValue(1);
        builder.Property(s => s.IsCurrent).IsRequired().HasDefaultValue(true);
        builder.Property(s => s.SupersededByPassportId);
        builder.Property(s => s.SupersededAtUtc);
        builder.Property(s => s.SupersedesPassportId);

        // Natural key. Inserting a duplicate (Code, Version) is a programming error in
        // the versioning service — the unique constraint is the database-level safety
        // net that converts it into a deterministic DbUpdateException.
        builder.HasIndex(s => new { s.Code, s.Version }).IsUnique();

        // Partial unique index — enforces "at most one current row per code" at the DB
        // layer so a racing publisher cannot create a duplicate even under the most
        // pathological optimistic-concurrency interleavings. The HasFilter clause uses
        // Postgres quoting because the column name is PascalCase.
        builder.HasIndex(s => s.Code)
            .IsUnique()
            .HasDatabaseName("IX_ServicePassports_Code_Current")
            .HasFilter("\"IsCurrent\" = true");

        builder.HasIndex(s => s.IsEnabled);

        // R0502 / TOR CF 01.05 — public catalog filters by Category; index supports the
        // equality match without sequential scans once the catalogue grows past trivial
        // size. Nullable column, so the index simply ignores legacy rows that have not
        // been categorised yet.
        builder.HasIndex(s => s.Category);
    }
}
