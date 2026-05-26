using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Audit;

/// <summary>
/// R0193 / TOR SEC 052 — server backend for the audit-log explorer. Provides three
/// surfaces: a paged QBE-filterable search, a grid export (CSV / XLSX / PDF), and an
/// archive-import re-attach for batches previously spilled to durable storage by
/// <see cref="IAuditArchive"/> (R0188).
/// </summary>
/// <remarks>
/// <para>
/// <b>Authorisation.</b> Every method is intended to be called from a
/// <c>cnas-admin</c>-gated controller — the audit log itself carries the data
/// every other registry produces, so exposing it broadly would re-leak PII the
/// audit infrastructure was designed to redact. The controller's
/// <c>[Authorize]</c> attribute is the primary gate; this interface does not
/// re-check roles (defence-in-depth would belong at the service layer if other
/// callers were added).
/// </para>
/// <para>
/// <b>Budget integration.</b> <see cref="SearchAsync"/> and
/// <see cref="ExportAsync"/> consult <see cref="Cnas.Ps.Application.QueryBudget.IQueryBudgetService"/> against the
/// <c>AuditLog</c> registry (tight 1000-row budget per R0167) BEFORE
/// materialising rows. Over-budget calls surface as
/// <see cref="ErrorCodes.QueryTooBroad"/> — the controller maps that to a 422
/// ProblemDetails carrying the verdict, identical to the rest of the
/// query-budget-gated registries.
/// </para>
/// <para>
/// <b>Sqid invariant.</b> Inbound DTOs never carry raw database ids; outbound
/// rows surface Sqid-encoded ids per CLAUDE.md RULE 3. The 8-char hash prefix
/// on <see cref="AuditLogRowDto"/> is not a Sqid — it is the first 8 chars of
/// the SHA-256 digest, exposed as a tamper-detection breadcrumb.
/// </para>
/// </remarks>
public interface IAuditExplorerService
{
    /// <summary>
    /// Paged QBE-filterable search over the audit-log table.
    /// </summary>
    /// <param name="input">Search envelope — optional QBE filter, optional date range, paging.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// On success a paged DTO carrying the Sqid-encoded rows and the total
    /// matching count. On failure one of:
    /// <list type="bullet">
    ///   <item><see cref="ErrorCodes.ValidationFailed"/> — input validator rejected the envelope.</item>
    ///   <item><see cref="ErrorCodes.QueryTooBroad"/> — budget guard refused; verdict on <see cref="LastBudgetVerdict"/>.</item>
    ///   <item>Any of the <c>QBE_*</c> family — converter rejected the QBE envelope.</item>
    /// </list>
    /// </returns>
    Task<Result<AuditLogPageDto>> SearchAsync(
        AuditLogSearchInput input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Exports the QBE-filtered audit-log rows in the requested format via the
    /// R0226 universal grid exporter. Uses the same column definitions exposed
    /// through <see cref="SearchAsync"/> so the file mirrors the on-screen
    /// grid.
    /// </summary>
    /// <param name="input">Same search envelope as <see cref="SearchAsync"/>.</param>
    /// <param name="format">Output format (CSV / XLSX / PDF).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// On success the rendered bytes wrapped in a <c>GridExportResult</c>. On
    /// failure one of: <see cref="ErrorCodes.QueryTooBroad"/>,
    /// <see cref="ErrorCodes.ExportTooLarge"/>,
    /// <see cref="ErrorCodes.ExportFormatNotSupported"/>, or
    /// <see cref="ErrorCodes.ValidationFailed"/>.
    /// </returns>
    Task<Result<Exports.GridExportResult>> ExportAsync(
        AuditLogSearchInput input,
        ExportFormat format,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-attaches the records of an archived audit batch (produced by
    /// <see cref="IAuditArchive.ArchiveAsync"/>, replayed manually by an
    /// operator) onto the live AuditLog table. Idempotent — duplicate rows
    /// (matched on <c>(EventAtUtc, EventCode, ActorId, TargetEntityId)</c>)
    /// are skipped, and the operator's caller-context audit row records the
    /// outcome via the <c>AUDIT.ARCHIVE.IMPORTED</c> Critical event.
    /// </summary>
    /// <param name="archiveKey">Opaque archive identifier returned by <see cref="IAuditArchive.ListPendingAsync"/>.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// On success a summary DTO carrying the imported / skipped counts and the
    /// archive's UTC span. On failure one of: <see cref="ErrorCodes.NotFound"/>
    /// (archive missing or quarantined empty) or <see cref="ErrorCodes.Internal"/>
    /// (replay raised an unexpected exception).
    /// </returns>
    Task<Result<AuditArchiveImportSummaryDto>> ImportArchiveAsync(
        string archiveKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Most-recent budget verdict captured during a <see cref="SearchAsync"/> or
    /// <see cref="ExportAsync"/> call. The controller reads this slot when
    /// surfacing a <see cref="ErrorCodes.QueryTooBroad"/> failure to populate
    /// the <c>extensions["budget"]</c> bag on the 422 ProblemDetails.
    /// </summary>
    QueryBudget.QueryBudgetVerdict? LastBudgetVerdict { get; }
}
