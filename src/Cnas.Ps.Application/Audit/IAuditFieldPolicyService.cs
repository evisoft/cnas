using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Audit;

/// <summary>
/// R0183 / SEC 043 — admin-facing CRUD surface over the
/// <see cref="Cnas.Ps.Core.Domain.AuditFieldPolicy"/> registry. Every mutating method
/// writes a Critical <c>AUDIT.FIELDPOLICY.{CREATED|UPDATED|DISABLED}</c> audit row
/// capturing the policy's <c>EntityType</c> so investigators can replay operator
/// activity.
/// </summary>
/// <remarks>
/// <para>
/// <b>Authorisation seam.</b> Mirrors <see cref="IAuditPolicyService"/> — the
/// controller applies the tech-admin authorization policy; this service only
/// guards against unauthenticated direct calls via
/// <see cref="Cnas.Ps.Application.Abstractions.ICallerContext.UserId"/> presence.
/// </para>
/// <para>
/// <b>Cache invalidation.</b> Every successful mutation triggers a
/// <see cref="IAuditFieldPolicyResolver"/>-side invalidation so the next diff-write
/// picks up the change without waiting for the 60 s background refresh. The
/// invalidation runs AFTER <c>SaveChangesAsync</c> succeeds so the resolver's
/// snapshot never reflects an uncommitted row.
/// </para>
/// <para>
/// <b>Sqid boundary.</b> Every method that emits or consumes an id uses the Sqid
/// string form per CLAUDE.md RULE 3; the raw <see cref="long"/> primary key never
/// appears on this surface. The natural-key
/// <see cref="AuditFieldPolicyOutput.EntityType"/> remains a raw stable string.
/// </para>
/// </remarks>
public interface IAuditFieldPolicyService
{
    /// <summary>
    /// Lists every active field-policy ordered by <c>EntityType</c> ascending.
    /// Soft-deleted rows are excluded.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>On success the materialised list.</returns>
    Task<Result<IReadOnlyList<AuditFieldPolicyOutput>>> ListAsync(CancellationToken ct = default);

    /// <summary>
    /// Fetches a single policy by its natural-key <c>EntityType</c>.
    /// </summary>
    /// <param name="entityType">CLR short name (e.g. <c>Solicitant</c>).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>On success the resolved policy; otherwise <see cref="ErrorCodes.NotFound"/>.</returns>
    Task<Result<AuditFieldPolicyOutput>> GetByEntityTypeAsync(string entityType, CancellationToken ct = default);

    /// <summary>
    /// Persists a new field policy. Idempotent on the unique <c>EntityType</c>: a
    /// second create with the same type returns <see cref="ErrorCodes.Conflict"/>.
    /// </summary>
    /// <param name="input">Create payload.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>On success the Sqid id of the persisted row.</returns>
    Task<Result<string>> CreateAsync(AuditFieldPolicyCreateInput input, CancellationToken ct = default);

    /// <summary>
    /// Updates an existing policy. <see cref="AuditFieldPolicyOutput.EntityType"/>
    /// is immutable — to "rename" disable the old row and create a new one.
    /// </summary>
    /// <param name="sqid">Sqid-encoded id of the policy.</param>
    /// <param name="input">Update payload.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success on apply; otherwise a structured failure.</returns>
    Task<Result> UpdateAsync(string sqid, AuditFieldPolicyUpdateInput input, CancellationToken ct = default);

    /// <summary>
    /// Soft-disables the policy (flips both <see cref="Cnas.Ps.Core.Domain.AuditFieldPolicy.IsEnabled"/>
    /// and <c>IsActive</c> to <c>false</c>) so the resolver stops returning matches.
    /// </summary>
    /// <param name="sqid">Sqid-encoded id of the policy.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success on apply; otherwise <see cref="ErrorCodes.NotFound"/>.</returns>
    Task<Result> DisableAsync(string sqid, CancellationToken ct = default);
}
