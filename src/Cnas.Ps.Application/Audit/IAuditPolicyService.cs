using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Audit;

/// <summary>
/// R0182 / SEC 042 — admin-facing CRUD surface over the
/// <see cref="Cnas.Ps.Core.Domain.AuditPolicy"/> registry. Every mutating method writes
/// a Critical <c>AUDIT.POLICY.{CREATED|UPDATED|DISABLED}</c> audit row capturing the
/// before/after policy code so investigators can replay operator activity.
/// </summary>
/// <remarks>
/// <para>
/// <b>Authorisation seam.</b> The service does NOT re-check the caller's role — that's
/// the controller's job (<c>AuditPoliciesController</c> applies the tech-admin
/// authorization policy). The service-layer guard is the explicit
/// <see cref="ErrorCodes.Unauthorized"/> failure when <c>ICallerContext.UserId</c> is
/// missing, which catches direct service callers that forgot to mock an authenticated
/// principal.
/// </para>
/// <para>
/// <b>Cache invalidation.</b> Every successful mutation triggers an
/// <see cref="IAuditPolicyResolver"/>-side cache invalidation so the next audit-event
/// write picks up the change without waiting for the 60 s background refresh. The
/// service performs the invalidation AFTER <c>SaveChangesAsync</c> succeeds so the
/// resolver's snapshot never reflects an uncommitted row.
/// </para>
/// <para>
/// <b>Sqid boundary.</b> Every method that emits or consumes an id uses the Sqid string
/// form per CLAUDE.md RULE 3; the raw <see cref="long"/> primary key never appears on
/// this surface. The natural-key <see cref="AuditPolicyOutput.Code"/> remains a raw
/// stable string.
/// </para>
/// </remarks>
public interface IAuditPolicyService
{
    /// <summary>
    /// Lists every active audit policy ordered by <see cref="AuditPolicyOutput.Priority"/>
    /// ascending, then by code ascending. Soft-deleted rows are excluded.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>On success the materialised list of <see cref="AuditPolicyOutput"/>.</returns>
    Task<Result<IReadOnlyList<AuditPolicyOutput>>> ListAsync(CancellationToken ct = default);

    /// <summary>
    /// Fetches a single policy by its natural-key <c>Code</c>. Useful for the admin UI
    /// when an operator types the code directly. Missing rows surface as
    /// <see cref="ErrorCodes.NotFound"/>.
    /// </summary>
    /// <param name="code">Natural-key code (e.g. <c>solicitant.view.search</c>).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>On success the resolved policy; otherwise <see cref="ErrorCodes.NotFound"/>.</returns>
    Task<Result<AuditPolicyOutput>> GetByCodeAsync(string code, CancellationToken ct = default);

    /// <summary>
    /// Persists a new audit policy. Idempotent on the unique-by-DB-index <c>Code</c>: a
    /// second create with the same code returns
    /// <see cref="ErrorCodes.Conflict"/> rather than silently overwriting — operators
    /// must explicitly <see cref="UpdateAsync"/> to mutate.
    /// </summary>
    /// <param name="input">Create payload.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// On success the Sqid id of the persisted row. Failures:
    /// <see cref="ErrorCodes.Unauthorized"/>, <see cref="ErrorCodes.ValidationFailed"/>,
    /// <see cref="ErrorCodes.Conflict"/>.
    /// </returns>
    Task<Result<string>> CreateAsync(AuditPolicyCreateInput input, CancellationToken ct = default);

    /// <summary>
    /// Updates an existing policy. <see cref="AuditPolicyOutput.Code"/> is immutable —
    /// to "rename" a policy operators disable the old row and create a new one. Missing
    /// rows return <see cref="ErrorCodes.NotFound"/>.
    /// </summary>
    /// <param name="sqid">Sqid-encoded id of the policy.</param>
    /// <param name="input">Update payload.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success on apply; otherwise a structured failure.</returns>
    Task<Result> UpdateAsync(string sqid, AuditPolicyUpdateInput input, CancellationToken ct = default);

    /// <summary>
    /// Soft-disables the policy (flips both <see cref="Cnas.Ps.Core.Domain.AuditPolicy.IsEnabled"/>
    /// and <c>IsActive</c> to <c>false</c>) so the resolver stops returning matches but
    /// the row remains queryable for audit forensics. The audit explorer (R0193) shows
    /// disabled rows in a separate filter.
    /// </summary>
    /// <param name="sqid">Sqid-encoded id of the policy.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success on apply; otherwise <see cref="ErrorCodes.NotFound"/>.</returns>
    Task<Result> DisableAsync(string sqid, CancellationToken ct = default);
}
