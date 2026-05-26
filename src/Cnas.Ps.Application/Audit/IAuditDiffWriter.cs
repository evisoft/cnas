using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Audit;

/// <summary>
/// R0183 / SEC 043 — facade for writing audit rows guarded by an
/// <see cref="Cnas.Ps.Core.Domain.AuditFieldPolicy"/>. Service-layer call sites
/// invoke <see cref="WriteIfDiffAsync{TEntity}"/> in place of
/// <see cref="Cnas.Ps.Application.UseCases.IAuditService.RecordAsync"/> when the
/// audit-worthiness of the event depends on whether tracked fields actually
/// changed.
/// </summary>
/// <remarks>
/// <para>
/// <b>No-policy fall-through.</b> When no <see cref="Cnas.Ps.Core.Domain.AuditFieldPolicy"/>
/// is configured for the supplied entity type, the writer behaves identically
/// to a regular <c>IAuditService.RecordAsync</c>
/// call — the audit row is still written with a minimal payload. This is the
/// no-behavioural-break invariant that lets call sites adopt
/// the diff writer without first ensuring every entity has
/// a configured policy.
/// </para>
/// <para>
/// <b>RequireAnyChange suppression.</b> When a policy IS configured AND
/// <see cref="AuditFieldPolicyView.RequireAnyChange"/> is true AND no tracked field
/// differs, the writer skips the audit row entirely and returns success without
/// touching the queue. The diff is also skipped — there is nothing to attach.
/// </para>
/// <para>
/// <b>Diff payload + PII redaction.</b> When the writer DOES emit, the resulting
/// JSON diff is fed through <see cref="PiiRedactor.Redact(string?)"/> so the R0194
/// hash chain reflects the on-disk shape exactly. Properties listed in the policy's
/// <see cref="AuditFieldPolicyView.SuppressedFields"/> have already been replaced
/// with <c>"[redacted]"</c> by the diff computer; the redactor adds the default
/// PII key list on top so any property happening to be named <c>email</c> /
/// <c>phone</c> / etc. is also covered.
/// </para>
/// </remarks>
public interface IAuditDiffWriter
{
    /// <summary>
    /// Writes an audit row conditioned on a tracked-field diff, falling through to
    /// a regular audit write when no policy is configured.
    /// </summary>
    /// <typeparam name="TEntity">
    /// CLR type of the entity. The writer uses the runtime
    /// <c>Type.Name</c> as the natural key against the policy table.
    /// </typeparam>
    /// <param name="eventCode">Stable event code (e.g. <c>SOLICITANT.UPDATED</c>).</param>
    /// <param name="entityId">
    /// Raw <see cref="long"/> primary key of the affected row. Encoded to Sqid form
    /// inside the writer before being placed onto the diff payload (per
    /// CLAUDE.md RULE 3).
    /// </param>
    /// <param name="before">Snapshot before the mutation. <c>null</c> models creation.</param>
    /// <param name="after">Snapshot after the mutation. <c>null</c> models deletion.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// Success when the audit was written (or skipped per
    /// <see cref="AuditFieldPolicyView.RequireAnyChange"/>); failure carries the
    /// audit-service error.
    /// </returns>
    Task<Result> WriteIfDiffAsync<TEntity>(
        string eventCode,
        long entityId,
        TEntity? before,
        TEntity? after,
        CancellationToken cancellationToken = default)
        where TEntity : class;
}
