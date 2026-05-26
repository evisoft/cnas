using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Application.Recalculation;

/// <summary>
/// R1503 / TOR §3.7-D — service façade for the legal-change-events registry.
/// Owns the register / modify / mark-ready / cancel lifecycle plus the
/// per-row lookup and list endpoints.
/// </summary>
/// <remarks>
/// <para>
/// <b>Audit attribution.</b> Every successful mutation emits a stable audit
/// event at <see cref="AuditSeverity.Critical"/> severity (legal-framework
/// changes are high-trust events):
/// <list type="bullet">
///   <item><see cref="RegisterAsync"/> → <c>LEGAL_CHANGE_EVENT.REGISTERED</c>.</item>
///   <item><see cref="ModifyAsync"/> → <c>LEGAL_CHANGE_EVENT.MODIFIED</c>.</item>
///   <item><see cref="MarkReadyAsync"/> → <c>LEGAL_CHANGE_EVENT.MARKED_READY</c>.</item>
///   <item><see cref="CancelAsync"/> → <c>LEGAL_CHANGE_EVENT.CANCELLED</c>.</item>
/// </list>
/// </para>
/// </remarks>
public interface ILegalChangeEventService
{
    /// <summary>
    /// Registers a new legal-change event. Generates <c>Code</c> as
    /// <c>LCE-{year}-{seq:000000}</c> when the caller omits it. When
    /// <c>Scope == All</c> the service snapshots every known
    /// <see cref="BenefitType"/> enum-name onto the row.
    /// </summary>
    /// <param name="input">Validated input envelope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Persisted DTO on success; validation / conflict failure otherwise.</returns>
    Task<Result<LegalChangeEventDto>> RegisterAsync(
        LegalChangeEventRegisterInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Modifies a legal-change event whose status is
    /// <see cref="LegalChangeEventStatus.Draft"/>. Refused otherwise with
    /// <see cref="ErrorCodes.Conflict"/>.
    /// </summary>
    /// <param name="sqid">Sqid-encoded event id.</param>
    /// <param name="input">Modify payload (one or more fields + ChangeReason).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Updated DTO on success; conflict / not-found / validation otherwise.</returns>
    Task<Result<LegalChangeEventDto>> ModifyAsync(
        string sqid,
        LegalChangeEventModifyInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Flips <see cref="LegalChangeEventStatus.Draft"/> →
    /// <see cref="LegalChangeEventStatus.Ready"/>. Validates the change
    /// payload JSON parses when non-null.
    /// </summary>
    /// <param name="sqid">Sqid-encoded event id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Updated DTO on success; conflict on non-Draft state.</returns>
    Task<Result<LegalChangeEventDto>> MarkReadyAsync(
        string sqid,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels a non-Applied legal-change event with a rationale.
    /// </summary>
    /// <param name="sqid">Sqid-encoded event id.</param>
    /// <param name="input">Reason payload (3..500 chars).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Updated DTO on success.</returns>
    Task<Result<LegalChangeEventDto>> CancelAsync(
        string sqid,
        LegalChangeEventReasonInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>Fetches a single event by its Sqid.</summary>
    /// <param name="sqid">Sqid-encoded event id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>DTO on success; not-found / invalid-sqid otherwise.</returns>
    Task<Result<LegalChangeEventDto>> GetByIdAsync(
        string sqid,
        CancellationToken cancellationToken = default);

    /// <summary>Lists events matching the supplied filter.</summary>
    /// <param name="filter">Filter envelope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Page DTO on success.</returns>
    Task<Result<LegalChangeEventPageDto>> ListAsync(
        LegalChangeEventFilterDto filter,
        CancellationToken cancellationToken = default);
}
