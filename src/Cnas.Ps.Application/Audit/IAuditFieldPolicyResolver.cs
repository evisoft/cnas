using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Application.Audit;

/// <summary>
/// R0183 / SEC 043 — synchronous resolver returning the effective per-entity field
/// policy. Mirrors the R0182 <see cref="IAuditPolicyResolver"/> shape: in-memory
/// snapshot rebuilt every 60 s by the companion hosted service plus explicit
/// invalidation on CRUD.
/// </summary>
/// <remarks>
/// <para>
/// <b>Hot-path discipline.</b> Implementations MUST be non-blocking — the diff
/// writer invokes <see cref="Resolve"/> once per mutating save. The reference
/// implementation backs this with a <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey, TValue}"/>
/// keyed by <see cref="AuditFieldPolicy.EntityType"/>.
/// </para>
/// <para>
/// <b>No-match contract.</b> When no enabled policy matches the supplied entity
/// type, the resolver returns <c>null</c>. Callers (<c>IAuditDiffWriter</c>) treat
/// <c>null</c> as "no policy configured" and fall through to a regular
/// <see cref="Cnas.Ps.Application.UseCases.IAuditService"/> write — no behavioural
/// break for entities that are not on the SEC 043 list.
/// </para>
/// </remarks>
public interface IAuditFieldPolicyResolver
{
    /// <summary>
    /// Resolves the effective per-entity field policy.
    /// </summary>
    /// <param name="entityType">
    /// CLR short name of the entity (e.g. <c>Solicitant</c>). Case-sensitive — must
    /// match the policy's stored <see cref="AuditFieldPolicy.EntityType"/> exactly.
    /// </param>
    /// <returns>
    /// The resolved policy view, or <c>null</c> when no enabled policy matches.
    /// </returns>
    AuditFieldPolicyView? Resolve(string entityType);
}
