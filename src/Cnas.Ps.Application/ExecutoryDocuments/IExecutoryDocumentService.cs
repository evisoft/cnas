using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Application.ExecutoryDocuments;

/// <summary>
/// R1600 / R1406 / TOR Annex 3.8 / §3.6-G — service façade for the executory-
/// documents (documente executorii) registry. Owns the register / modify /
/// suspend / resume / cancel / complete lifecycle plus the
/// running-tally accumulation that drives the auto-complete transition.
/// </summary>
/// <remarks>
/// <para>
/// <b>Audit attribution.</b> Every successful mutation emits a stable audit
/// event at <see cref="AuditSeverity.Critical"/> severity (PII / financial
/// data — see CLAUDE.md §5.6):
/// <list type="bullet">
///   <item><see cref="RegisterAsync"/> → <c>EXECUTORY_DOC.REGISTERED</c>.</item>
///   <item><see cref="ModifyAsync"/> → <c>EXECUTORY_DOC.MODIFIED</c>.</item>
///   <item><see cref="SuspendAsync"/> → <c>EXECUTORY_DOC.SUSPENDED</c>.</item>
///   <item><see cref="ResumeAsync"/> → <c>EXECUTORY_DOC.RESUMED</c>.</item>
///   <item><see cref="CancelAsync"/> → <c>EXECUTORY_DOC.CANCELLED</c>.</item>
///   <item><see cref="CompleteAsync"/> → <c>EXECUTORY_DOC.COMPLETED</c>.</item>
///   <item><see cref="RecordWithholdingAsync"/> → <c>EXECUTORY_DOC.WITHHELD</c> at <see cref="AuditSeverity.Information"/>.</item>
/// </list>
/// </para>
/// <para>
/// <b>Sqids everywhere.</b> Identifiers crossing the boundary are Sqid-encoded
/// per CLAUDE.md RULE 3; the service decodes them internally before touching
/// the DbContext. IDNP / IBAN are NOT Sqids — they are stable external
/// identifiers that the application encrypts at rest.
/// </para>
/// </remarks>
public interface IExecutoryDocumentService
{
    /// <summary>
    /// R1600 — registers a new executory document. Generates
    /// <c>DocumentSeriesNumber</c> server-side in the format
    /// <c>EXE-{year}-{seq:000000}</c> when the caller does not supply one.
    /// </summary>
    /// <param name="input">Validated input envelope.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// On success the persisted <see cref="ExecutoryDocumentDto"/>; on
    /// validation failure <see cref="ErrorCodes.ValidationFailed"/>; on
    /// duplicate series number <see cref="ErrorCodes.Conflict"/>.
    /// </returns>
    Task<Result<ExecutoryDocumentDto>> RegisterAsync(
        ExecutoryDocumentRegisterInputDto input,
        CancellationToken ct = default);

    /// <summary>
    /// R1600 — modifies an outstanding document. Refused when status is
    /// <see cref="ExecutoryDocumentStatus.Completed"/> or
    /// <see cref="ExecutoryDocumentStatus.Cancelled"/>.
    /// </summary>
    /// <param name="sqid">Sqid-encoded document id.</param>
    /// <param name="input">Modify payload (one or more fields + ChangeReason).</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// On success the updated DTO; on cancelled/completed row
    /// <see cref="ErrorCodes.Conflict"/>; on missing row
    /// <see cref="ErrorCodes.NotFound"/>; on validation failure
    /// <see cref="ErrorCodes.ValidationFailed"/>.
    /// </returns>
    Task<Result<ExecutoryDocumentDto>> ModifyAsync(
        string sqid,
        ExecutoryDocumentModifyInputDto input,
        CancellationToken ct = default);

    /// <summary>
    /// R1600 — flips <see cref="ExecutoryDocumentStatus.Active"/> →
    /// <see cref="ExecutoryDocumentStatus.Suspended"/> with a rationale.
    /// </summary>
    /// <param name="sqid">Sqid-encoded document id.</param>
    /// <param name="input">Reason payload (3..500 chars).</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>Updated DTO on success; conflict / not-found / validation-failed otherwise.</returns>
    Task<Result<ExecutoryDocumentDto>> SuspendAsync(
        string sqid,
        ExecutoryDocumentReasonInputDto input,
        CancellationToken ct = default);

    /// <summary>
    /// R1600 — flips <see cref="ExecutoryDocumentStatus.Suspended"/> →
    /// <see cref="ExecutoryDocumentStatus.Active"/> with a rationale.
    /// </summary>
    /// <param name="sqid">Sqid-encoded document id.</param>
    /// <param name="input">Reason payload (3..500 chars).</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>Updated DTO on success; conflict / not-found / validation-failed otherwise.</returns>
    Task<Result<ExecutoryDocumentDto>> ResumeAsync(
        string sqid,
        ExecutoryDocumentReasonInputDto input,
        CancellationToken ct = default);

    /// <summary>
    /// R1600 — flips Active or Suspended →
    /// <see cref="ExecutoryDocumentStatus.Cancelled"/>. Records the rationale
    /// on <c>CancellationReason</c>.
    /// </summary>
    /// <param name="sqid">Sqid-encoded document id.</param>
    /// <param name="input">Reason payload (3..500 chars).</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>Updated DTO on success; conflict / not-found / validation-failed otherwise.</returns>
    Task<Result<ExecutoryDocumentDto>> CancelAsync(
        string sqid,
        ExecutoryDocumentReasonInputDto input,
        CancellationToken ct = default);

    /// <summary>
    /// R1600 — flips <see cref="ExecutoryDocumentStatus.Active"/> →
    /// <see cref="ExecutoryDocumentStatus.Completed"/> when
    /// <c>TotalWithheldMdl &gt;= TotalOwedMdl</c> (or operator-forced when
    /// <c>TotalOwedMdl</c> is null).
    /// </summary>
    /// <param name="sqid">Sqid-encoded document id.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>Updated DTO on success; conflict / not-found otherwise.</returns>
    Task<Result<ExecutoryDocumentDto>> CompleteAsync(string sqid, CancellationToken ct = default);

    /// <summary>
    /// R1600 / R1406 — appends an amount to <c>TotalWithheldMdl</c> on the
    /// document row. When the running total reaches <c>TotalOwedMdl</c> the
    /// document auto-flips to <see cref="ExecutoryDocumentStatus.Completed"/>
    /// and a Critical <c>EXECUTORY_DOC.COMPLETED</c> audit row is emitted in
    /// addition to the per-withholding <c>EXECUTORY_DOC.WITHHELD</c>
    /// Information row.
    /// </summary>
    /// <param name="sqid">Sqid-encoded document id.</param>
    /// <param name="amountMdl">Amount actually withheld (MDL). Must be &gt; 0.</param>
    /// <param name="sourceReference">Free-form reference identifying the payment that produced the withholding (e.g. <c>UNEMPLOYMENT.PAYMENT.{paymentSqid}</c>).</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>Updated DTO on success; not-found / conflict / validation-failed otherwise.</returns>
    Task<Result<ExecutoryDocumentDto>> RecordWithholdingAsync(
        string sqid,
        decimal amountMdl,
        string sourceReference,
        CancellationToken ct = default);

    /// <summary>Fetches a single document by Sqid id.</summary>
    /// <param name="sqid">Sqid-encoded document id.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>The DTO when found; <see cref="ErrorCodes.NotFound"/> otherwise.</returns>
    Task<Result<ExecutoryDocumentDto>> GetByIdAsync(string sqid, CancellationToken ct = default);

    /// <summary>
    /// Lists every non-deleted document targeting the supplied debtor IDNP,
    /// ordered by <c>PriorityRank ASC</c> then <c>IssuedDate DESC</c>.
    /// </summary>
    /// <param name="debtorIdnp">Plaintext IDNP — the service hashes internally for the lookup.</param>
    /// <param name="statusFilter">Optional status restriction; null = all statuses.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>An ordered list (may be empty).</returns>
    Task<Result<IReadOnlyList<ExecutoryDocumentDto>>> ListByDebtorAsync(
        string debtorIdnp,
        ExecutoryDocumentStatusFilter? statusFilter,
        CancellationToken ct = default);
}

/// <summary>
/// R1600 — list-filter envelope for <see cref="IExecutoryDocumentService.ListByDebtorAsync"/>.
/// Carries a single optional status restriction. Modelled as a record so the
/// "no filter" case is the default-constructed value.
/// </summary>
/// <param name="Status">Optional status restriction (Active / Suspended / Completed / Cancelled).</param>
public sealed record ExecutoryDocumentStatusFilter(ExecutoryDocumentStatus Status);
