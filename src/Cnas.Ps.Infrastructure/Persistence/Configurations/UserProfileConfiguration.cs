using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>Maps <see cref="UserProfile"/> to <c>cnas.UserProfiles</c>.</summary>
public sealed class UserProfileConfiguration : AuditableEntityConfiguration<UserProfile>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<UserProfile> builder)
    {
        builder.ToTable("UserProfiles");

        builder.Property(u => u.MPassSubject).HasMaxLength(128);
        builder.Property(u => u.LocalLogin).HasMaxLength(128);
        builder.Property(u => u.LocalPasswordHash).HasMaxLength(512);
        // NationalId is encrypted at rest — widened from VARCHAR(13) to VARCHAR(128) to hold
        // the v1: envelope. See Solicitant.NationalId for the size rationale.
        builder.Property(u => u.NationalId).HasMaxLength(128);
        builder.Property(u => u.DisplayName).IsRequired().HasMaxLength(256);
        builder.Property(u => u.Email).HasMaxLength(320);
        // PhoneE164 is encrypted at rest (CLAUDE.md §5.7 / TOR SEC 035). The 15-digit
        // canonical plaintext widens to ~59 chars of v1: envelope ciphertext, so the
        // column matches the other encrypted-string columns (NationalId / Idnp / Idno)
        // at VARCHAR(128) for v2 envelope headroom. The converter wiring lives in
        // CnasDbContext.OnModelCreating because the encryptor is injected on the context.
        // No index — equality on encrypted columns is useless; phone is a display field
        // so no hash shadow column is provided (unlike the national-identifier pattern).
        builder.Property(u => u.PhoneE164).HasMaxLength(128);
        builder.Property(u => u.PreferredLanguage).IsRequired().HasMaxLength(8);

        // R0171 / CF 22.02 / CF 04.08 — JSON-serialised per-channel opt-in flags.
        // Nullable: NULL means "default opt-in for every channel" (see entity remarks).
        // Stored as PostgreSQL jsonb for compact storage and future indexability; the
        // InMemory provider used in tests treats it as a regular nullable string column.
        builder.Property(u => u.NotificationPreferences).HasColumnType("jsonb");

        // R0535 / CF 04.07-08 — JSON-serialised UI layout preferences. Nullable: NULL means
        // "use system defaults" (see entity remarks). Stored as PostgreSQL jsonb for
        // compact storage and future indexability; the InMemory provider used in tests
        // treats it as a regular nullable string column.
        builder.Property(u => u.LayoutPreferences).HasColumnType("jsonb");

        // Shadow column for equality lookups on the encrypted NationalId. Nullable to mirror
        // the plaintext column's nullability. Base64(HMAC-SHA256) = 44.
        builder.Property(u => u.NationalIdHash).HasMaxLength(44);

        // PostgreSQL text[] columns for roles/groups — efficient containment queries.
        builder.Property(u => u.Roles).HasColumnType("text[]");
        builder.Property(u => u.Groups).HasColumnType("text[]");

        // R0059 / SEC 016 — Account state machine. Stored as a non-nullable integer with
        // a server-side default of 0 (UserAccountState.Active) so existing-row back-fill
        // is implicit on column add. EF Core handles the enum→int conversion implicitly
        // (no value converter required for int-backed enums).
        builder.Property(u => u.State)
            .IsRequired()
            .HasDefaultValue(UserAccountState.Active);

        builder.HasIndex(u => u.MPassSubject).IsUnique().HasFilter("\"MPassSubject\" IS NOT NULL");
        builder.HasIndex(u => u.LocalLogin).IsUnique().HasFilter("\"LocalLogin\" IS NOT NULL");
        // Lookup index moves to the hash column — the encrypted plaintext column would be
        // useless for equality (random nonce per encryption) and there's no benefit to keeping
        // an index on it. The hash-column index is non-unique because two UserProfiles MAY
        // share a NationalId in principle (e.g. a single citizen with both a legacy local
        // login and an MPass-linked profile — see SEC 014).
        builder.HasIndex(u => u.NationalIdHash);

        // Non-clustered index on State supports the "list users by lifecycle state" admin
        // queries (e.g. "show me every Suspended user", future bulk-reactivation pipelines).
        // Low-cardinality but the underlying read pattern is dominated by selective state
        // filters where the planner benefits from a dedicated index.
        builder.HasIndex(u => u.State);
    }
}
