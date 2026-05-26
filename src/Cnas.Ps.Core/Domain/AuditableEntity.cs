namespace Cnas.Ps.Core.Domain;

/// <summary>
/// Base type for any entity that participates in TOR audit/trasabilitate (SEC 042).
/// Provides the system-assigned primary key, creation/modification metadata, and
/// the soft-delete flag mandated by CLAUDE.md cross-cutting principles.
/// </summary>
public abstract class AuditableEntity
{
    /// <summary>Internal 64-bit identity. Never leaves the system — externalize via <c>ISqidService</c>.</summary>
    public long Id { get; set; }

    /// <summary>UTC timestamp when the record was created (TOR ARH — UTF-8 + UTC).</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>Identifier (Sqid string is fine for users; raw long for system jobs) of the actor that created the record.</summary>
    public string? CreatedBy { get; set; }

    /// <summary>UTC timestamp of the last modification.</summary>
    public DateTime? UpdatedAtUtc { get; set; }

    /// <summary>Identifier of the actor that performed the last modification.</summary>
    public string? UpdatedBy { get; set; }

    /// <summary>
    /// Soft-delete flag. True until the record is logically removed; CLAUDE.md mandates
    /// soft delete for any business-meaningful entity. Hard deletes are reserved for
    /// transient data or GDPR right-to-erasure requests.
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Optimistic concurrency token, populated by EF Core from a Postgres <c>xmin</c>.</summary>
    public uint Xmin { get; set; }
}
