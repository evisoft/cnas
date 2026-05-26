using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.LaborBooklet;

/// <summary>
/// R0920 / R0921 / TOR BP 2.3 — service façade for the labor-booklet
/// (Carnet de muncă) registry plus its child pre-01.01.1999 activity-period
/// rows.
/// </summary>
/// <remarks>
/// <para>
/// Each operation emits one audit row at the documented severity. Registration,
/// scanned-copy attachment, period add / amend / close emit Notice; the
/// terminal verify / reject transitions emit Critical because they confirm
/// that a citizen's pre-1999 contribution history is admissible (or not) for
/// future pension calculations.
/// </para>
/// <para>
/// All identifiers crossing the boundary are Sqid-encoded per CLAUDE.md
/// RULE 3; internally the service decodes them to raw <c>long</c> primary keys
/// before touching the DbContext.
/// </para>
/// </remarks>
public interface ILaborBookletService
{
    /// <summary>
    /// R0920 / BP 2.3-A — registers a fresh labor-booklet master row in the
    /// <c>Pending</c> state. Emits a Notice audit <c>LABOR_BOOKLET.REGISTERED</c>.
    /// </summary>
    /// <param name="input">Validated input envelope.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// On success the persisted <see cref="LaborBookletDto"/>; on duplicate
    /// per-citizen booklet number <see cref="ErrorCodes.Conflict"/> with the
    /// stable message <c>LABOR_BOOKLET_DUPLICATE</c>; on validation failure
    /// <see cref="ErrorCodes.ValidationFailed"/>; on unknown citizen
    /// <see cref="ErrorCodes.NotFound"/>.
    /// </returns>
    Task<Result<LaborBookletDto>> RegisterAsync(
        LaborBookletRegisterInputDto input,
        CancellationToken ct = default);

    /// <summary>
    /// R0920 / BP 2.3-A — attaches a scanned copy of the paper booklet plus
    /// optional OCR metadata. Uploads via the R0227 attachment surface with
    /// <c>OwnerEntityType="LaborBooklet"</c>; sets the row's
    /// <c>HasScannedCopy</c> flag and persists any supplied OCR fields.
    /// Emits a Notice audit <c>LABOR_BOOKLET.SCANNED_COPY_ATTACHED</c>.
    /// </summary>
    /// <param name="bookletId">Raw bigint id of the target row.</param>
    /// <param name="input">Upload payload (file + optional OCR metadata).</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// On success the refreshed DTO; on missing row
    /// <see cref="ErrorCodes.NotFound"/>; on rejected row
    /// <see cref="ErrorCodes.Conflict"/>; on malformed payload
    /// <see cref="ErrorCodes.ValidationFailed"/>; on upload failure the stable
    /// error code surfaced by the attachment service.
    /// </returns>
    Task<Result<LaborBookletDto>> AttachScannedCopyAsync(
        long bookletId,
        ScannedCopyAttachmentInputDto input,
        CancellationToken ct = default);

    /// <summary>
    /// R0920 / BP 2.3-A — transitions a Pending booklet to <c>Verified</c>.
    /// Captures the operator's user id + UTC timestamp. Emits a Critical audit
    /// <c>LABOR_BOOKLET.VERIFIED</c>.
    /// </summary>
    /// <param name="bookletId">Raw bigint id of the booklet.</param>
    /// <param name="notes">Optional verifier note (3..500 chars when supplied).</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// On success the updated DTO; on missing row
    /// <see cref="ErrorCodes.NotFound"/>; on non-Pending state
    /// <see cref="ErrorCodes.Conflict"/>; on bad notes
    /// <see cref="ErrorCodes.ValidationFailed"/>.
    /// </returns>
    Task<Result<LaborBookletDto>> VerifyAsync(
        long bookletId,
        string? notes,
        CancellationToken ct = default);

    /// <summary>
    /// R0920 / BP 2.3-A — transitions a Pending booklet to <c>Rejected</c>.
    /// Captures the rejection rationale + UTC timestamp. Emits a Critical audit
    /// <c>LABOR_BOOKLET.REJECTED</c>.
    /// </summary>
    /// <param name="bookletId">Raw bigint id of the booklet.</param>
    /// <param name="reason">Operator rationale (3..500 chars).</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// On success the updated DTO; on missing row
    /// <see cref="ErrorCodes.NotFound"/>; on non-Pending state
    /// <see cref="ErrorCodes.Conflict"/>; on bad reason
    /// <see cref="ErrorCodes.ValidationFailed"/>.
    /// </returns>
    Task<Result<LaborBookletDto>> RejectAsync(
        long bookletId,
        string reason,
        CancellationToken ct = default);

    /// <summary>
    /// R0921 / BP 2.3-B — adds a new pre-1999 activity-period row linked to the
    /// supplied booklet. Emits a Notice audit <c>PRE1999_PERIOD.ADDED</c>.
    /// </summary>
    /// <param name="bookletId">Raw bigint id of the sourcing booklet.</param>
    /// <param name="period">Validated input envelope.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// On success <see cref="Result.Success"/>; on missing booklet
    /// <see cref="ErrorCodes.NotFound"/>; on rejected booklet
    /// <see cref="ErrorCodes.Conflict"/>; on validation failure
    /// <see cref="ErrorCodes.ValidationFailed"/>.
    /// </returns>
    Task<Result> AddPeriodAsync(
        long bookletId,
        InsuredPersonPre1999PeriodInputDto period,
        CancellationToken ct = default);

    /// <summary>
    /// R0921 / BP 2.3-B — supersedes an existing period row R0301-style:
    /// closes the previous row (sets <c>ValidToUtc</c>) and inserts a fresh
    /// row with the new attributes. Emits a Notice audit
    /// <c>PRE1999_PERIOD.AMENDED</c>.
    /// </summary>
    /// <param name="periodId">Raw bigint id of the row to amend.</param>
    /// <param name="input">Validated input envelope with the new attributes.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// On success <see cref="Result.Success"/>; on missing row
    /// <see cref="ErrorCodes.NotFound"/>; on validation failure
    /// <see cref="ErrorCodes.ValidationFailed"/>; on already-closed row
    /// <see cref="ErrorCodes.Conflict"/>.
    /// </returns>
    Task<Result> AmendPeriodAsync(
        long periodId,
        InsuredPersonPre1999PeriodInputDto input,
        CancellationToken ct = default);

    /// <summary>
    /// R0921 / BP 2.3-B — closes an existing period row by stamping
    /// <c>ValidToUtc</c>. Emits a Notice audit <c>PRE1999_PERIOD.CLOSED</c>.
    /// </summary>
    /// <param name="periodId">Raw bigint id of the row to close.</param>
    /// <param name="reason">Operator rationale (3..500 chars).</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// On success <see cref="Result.Success"/>; on missing row
    /// <see cref="ErrorCodes.NotFound"/>; on already-closed row
    /// <see cref="ErrorCodes.Conflict"/>; on bad reason
    /// <see cref="ErrorCodes.ValidationFailed"/>.
    /// </returns>
    Task<Result> ClosePeriodAsync(
        long periodId,
        string reason,
        CancellationToken ct = default);

    /// <summary>
    /// Fetches a single booklet row by raw id; returns <see langword="null"/>
    /// when the row is missing or soft-deleted.
    /// </summary>
    /// <param name="bookletId">Raw bigint id of the booklet.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>The DTO or <see langword="null"/>.</returns>
    Task<LaborBookletDto?> GetAsync(long bookletId, CancellationToken ct = default);

    /// <summary>
    /// Lists every active (non-closed) pre-1999 period row for the supplied
    /// citizen, ordered by ascending <c>PeriodStartDate</c>.
    /// </summary>
    /// <param name="insuredPersonId">Raw bigint id of the natural-person Solicitant.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>An ordered list — empty when the citizen has no rows.</returns>
    Task<IReadOnlyList<InsuredPersonPre1999PeriodDto>> ListPeriodsForInsuredPersonAsync(
        long insuredPersonId,
        CancellationToken ct = default);
}
