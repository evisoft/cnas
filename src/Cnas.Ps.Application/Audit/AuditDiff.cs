using System.Collections.Generic;

namespace Cnas.Ps.Application.Audit;

/// <summary>
/// R0183 / SEC 043 — single tracked-field change captured by
/// <see cref="IAuditDiffComputer.Compute"/>. The before / after values are stored
/// as JSON strings so primitives, enums, <see cref="System.DateTime"/>, GUIDs, and
/// nested objects all round-trip without bespoke per-type serialisers downstream.
/// </summary>
/// <param name="PropertyName">
/// Exact CLR property name (e.g. <c>DisplayName</c>, <c>Email</c>). Mirrors the
/// strings declared on <see cref="Cnas.Ps.Core.Domain.AuditFieldPolicy.TrackedFields"/>.
/// </param>
/// <param name="BeforeJson">
/// JSON-encoded representation of the property's value on the before snapshot. The
/// literal text <c>null</c> means "field was JSON-null"; a .NET <c>null</c> means
/// "the snapshot itself was null" (entity-creation case — every tracked field
/// surfaces with <see cref="BeforeJson"/> = <c>null</c>). When the property is
/// listed in <see cref="Cnas.Ps.Core.Domain.AuditFieldPolicy.SuppressedFields"/>
/// the value is the literal string <c>"\"[redacted]\""</c>.
/// </param>
/// <param name="AfterJson">
/// JSON-encoded representation of the property's value on the after snapshot.
/// Same null / redaction conventions as <see cref="BeforeJson"/>; the deletion case
/// (<c>after == null</c>) surfaces every tracked field with <see cref="AfterJson"/>
/// = <c>null</c>.
/// </param>
public sealed record AuditDiffEntry(
    string PropertyName,
    string? BeforeJson,
    string? AfterJson);

/// <summary>
/// R0183 / SEC 043 — structured before/after diff produced by
/// <see cref="IAuditDiffComputer.Compute"/> for one mutating save on an entity
/// covered by an <see cref="Cnas.Ps.Core.Domain.AuditFieldPolicy"/>. Persisted as
/// JSON on the audit row's <c>DetailsJson</c> column AFTER
/// <see cref="PiiRedactor.Redact(string?)"/> normalisation so the R0194 hash chain
/// reflects the on-disk shape.
/// </summary>
/// <param name="EntityType">
/// CLR short name of the entity (e.g. <c>Solicitant</c>). Echoed from the matched
/// <see cref="Cnas.Ps.Core.Domain.AuditFieldPolicy.EntityType"/> verbatim so the
/// downstream consumer can validate the diff is for the expected type.
/// </param>
/// <param name="EntityId">
/// Sqid-encoded external id of the affected row per CLAUDE.md RULE 3. NEVER the
/// raw <see cref="long"/> primary key — the diff payload reaches the audit log
/// (and from there potentially the audit explorer / SIEM forwarder) and crosses
/// the system boundary.
/// </param>
/// <param name="Entries">
/// The list of changed-field entries, in the order tracked-fields are declared on
/// the policy. Empty when no tracked field differs and
/// <see cref="Cnas.Ps.Core.Domain.AuditFieldPolicy.RequireAnyChange"/> is false;
/// when <see cref="Cnas.Ps.Core.Domain.AuditFieldPolicy.RequireAnyChange"/> is
/// true and no field differs, the computer returns <c>null</c> instead of an
/// empty diff so the caller can skip the audit-write entirely.
/// </param>
public sealed record AuditDiff(
    string EntityType,
    string EntityId,
    IReadOnlyList<AuditDiffEntry> Entries);

/// <summary>
/// R0183 / SEC 043 — snapshot of an <see cref="Cnas.Ps.Core.Domain.AuditFieldPolicy"/>
/// row materialised by <see cref="IAuditFieldPolicyResolver"/> from the in-memory
/// cache. Distinct from the entity so the cache layer can expose case-insensitive
/// hashset projections (<see cref="TrackedFields"/> + <see cref="SuppressedFields"/>)
/// without re-allocating on every lookup.
/// </summary>
/// <param name="EntityType">CLR short name natural-key.</param>
/// <param name="TrackedFields">
/// Case-insensitive set of property names whose changes trigger a diff entry. The
/// resolver materialises this as a <see cref="HashSet{T}"/> with
/// <see cref="System.StringComparer.Ordinal"/> — CLR property names are
/// case-sensitive, mirroring how the reflection-based diff computer keys off them.
/// </param>
/// <param name="SuppressedFields">
/// Case-sensitive set of property names whose values must be redacted in the diff
/// payload. Overlap with <see cref="TrackedFields"/> is meaningful — see
/// <see cref="Cnas.Ps.Core.Domain.AuditFieldPolicy"/> remarks.
/// </param>
/// <param name="RequireAnyChange">
/// When <c>true</c>, the writer skips the audit row when no tracked field differs.
/// </param>
/// <param name="Severity">
/// Severity stamped on the emitted audit row (subject to further R0182
/// <see cref="Cnas.Ps.Core.Domain.AuditPolicy"/> resolution on the hot path).
/// </param>
public sealed record AuditFieldPolicyView(
    string EntityType,
    IReadOnlySet<string> TrackedFields,
    IReadOnlySet<string> SuppressedFields,
    bool RequireAnyChange,
    Cnas.Ps.Core.Domain.AuditSeverity Severity);

/// <summary>
/// R0183 / SEC 043 — synchronous, reflection-based computer that produces a
/// structured before/after diff for one mutating save. Single method to keep the
/// API surface narrow; implementations are pure (no DI, no I/O) so the diff
/// computation is deterministic and trivially unit-testable.
/// </summary>
/// <remarks>
/// <para>
/// <b>Null-snapshot contract.</b> <c>before == null</c> models creation — every
/// tracked field appears with <see cref="AuditDiffEntry.BeforeJson"/> = <c>null</c>.
/// <c>after == null</c> models deletion (or a soft-delete flip) — every tracked
/// field appears with <see cref="AuditDiffEntry.AfterJson"/> = <c>null</c>. Both
/// nulls are an invariant violation; the implementation throws
/// <see cref="System.ArgumentNullException"/> in that case.
/// </para>
/// <para>
/// <b>Equality semantics.</b> Primitives and strings compare with
/// <see cref="object.Equals(object?, object?)"/>; <see cref="System.DateTime"/>
/// collapses to UTC before comparison; collection-shaped properties (anything
/// implementing <see cref="System.Collections.IEnumerable"/> other than
/// <see cref="string"/>) compare by JSON-serialised shape — cheap and sufficient
/// for the audit purpose.
/// </para>
/// </remarks>
public interface IAuditDiffComputer
{
    /// <summary>
    /// Computes the diff for a single mutating save.
    /// </summary>
    /// <param name="entityType">
    /// CLR short name expected on the policy — used to populate
    /// <see cref="AuditDiff.EntityType"/>. The implementation does NOT cross-check
    /// the supplied policy's <see cref="AuditFieldPolicyView.EntityType"/>; the
    /// caller (<c>IAuditDiffWriter</c>) is responsible for matching.
    /// </param>
    /// <param name="before">
    /// Snapshot of the entity before the mutation. <c>null</c> models creation.
    /// </param>
    /// <param name="after">
    /// Snapshot of the entity after the mutation. <c>null</c> models deletion.
    /// </param>
    /// <param name="policy">Resolved policy view that drives tracking + suppression.</param>
    /// <returns>
    /// On change → an <see cref="AuditDiff"/> with the changed entries (suppressed
    /// fields are present but redacted). On no-change AND
    /// <see cref="AuditFieldPolicyView.RequireAnyChange"/> = true → <c>null</c>
    /// (caller skips the audit-write). On no-change AND
    /// <see cref="AuditFieldPolicyView.RequireAnyChange"/> = false → an
    /// <see cref="AuditDiff"/> with an empty <see cref="AuditDiff.Entries"/> list.
    /// </returns>
    AuditDiff? Compute(string entityType, object? before, object? after, AuditFieldPolicyView policy);
}
