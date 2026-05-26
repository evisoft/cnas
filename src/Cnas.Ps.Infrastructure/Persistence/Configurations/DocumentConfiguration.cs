using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>Maps <see cref="Document"/> to <c>cnas.Documents</c>.</summary>
public sealed class DocumentConfiguration : AuditableEntityConfiguration<Document>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<Document> builder)
    {
        builder.ToTable("Documents");

        builder.Property(d => d.Kind).IsRequired().HasConversion<int>();
        builder.Property(d => d.Title).IsRequired().HasMaxLength(256);
        builder.Property(d => d.MimeType).IsRequired().HasMaxLength(128);
        builder.Property(d => d.StorageObjectKey).IsRequired().HasMaxLength(256);
        builder.Property(d => d.StorageBucket).IsRequired().HasMaxLength(64);
        builder.Property(d => d.ContentSha256Hex).IsRequired().HasMaxLength(64);
        builder.Property(d => d.SignatureObjectKey).HasMaxLength(256);

        // UC08 examiner verdict fields — added in migration AddDocumentVerdictFields.
        builder.Property(d => d.Verdict);
        builder.Property(d => d.VerdictNote).HasMaxLength(1024);
        builder.Property(d => d.VerdictAtUtc);

        builder.HasIndex(d => d.DossierId);
        builder.HasIndex(d => d.ContentSha256Hex);
    }
}
