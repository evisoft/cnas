using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="UserSession"/> to <c>cnas.UserSessions</c> — backs the R2264
/// concurrent-session-limit + R2267 manual / auto session-lock primitives. Column caps
/// mirror the entity XML doc; indexes back the two hot-paths (per-user "active sessions"
/// list, by-id middleware lookup).
/// </summary>
/// <remarks>
/// <para>
/// Two indexes are configured on top of the two contributed by
/// <see cref="AuditableEntityConfiguration{TEntity}"/>:
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///       <c>UNIQUE(SessionId)</c> — every opaque session token is unique; the
///       middleware's hot path resolves a request's <c>jti</c> straight to the row
///       via this index.
///     </description>
///   </item>
///   <item>
///     <description>
///       <c>(UserUserId, IsTerminated, CreatedAtUtc DESC)</c> — the
///       limit-enforcer's "newest live sessions for this user" probe plus the
///       per-caller "list my active sessions" surface both pivot on this triple.
///       Descending on <c>UserSession.CreatedAtUtc</c> (inherited from
///       <see cref="AuditableEntity"/>) means the index naturally returns
///       sessions newest-first.
///     </description>
///   </item>
/// </list>
/// </remarks>
public sealed class UserSessionConfiguration : AuditableEntityConfiguration<UserSession>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<UserSession> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("UserSessions");

        builder.Property(s => s.UserUserId).IsRequired();
        builder.Property(s => s.SessionId).IsRequired().HasMaxLength(128);
        builder.Property(s => s.IpAddress).HasMaxLength(64);
        builder.Property(s => s.UserAgent).HasMaxLength(512);
        builder.Property(s => s.LastActivityUtc).IsRequired();
        builder.Property(s => s.IsLocked).IsRequired().HasDefaultValue(false);
        builder.Property(s => s.IsTerminated).IsRequired().HasDefaultValue(false);
        builder.Property(s => s.TerminationReason).HasMaxLength(64);

        builder.HasIndex(s => s.SessionId).IsUnique();

        builder.HasIndex(s => new { s.UserUserId, s.IsTerminated, s.CreatedAtUtc })
            .HasDatabaseName("IX_UserSessions_User_Terminated_CreatedDesc")
            .IsDescending(false, false, true);
    }
}
