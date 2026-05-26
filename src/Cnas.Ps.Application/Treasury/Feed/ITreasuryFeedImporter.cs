using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Application.Treasury.Feed;

/// <summary>
/// R1810 / TOR BP 1.2-I — orchestrator that fetches, parses, and projects a
/// daily Treasury feed into <c>TreasuryPaymentReceipt</c> rows. Called both
/// by the nightly Quartz job (TriggerKind=Scheduled) and the admin REST
/// surface (TriggerKind=Manual).
/// </summary>
/// <remarks>
/// <para>
/// <b>Algorithm.</b>
/// <list type="number">
///   <item>Insert a <c>TreasuryFeedImport</c> row, Status=Pending.</item>
///   <item>Flip Status=Downloading and call <see cref="ITreasuryFeedSource.FetchAsync"/>.</item>
///   <item>Flip Status=Parsing and call <see cref="ITreasuryFeedParser.ParseAsync"/>.</item>
///   <item>Flip Status=Importing and project each row into a <c>TreasuryPaymentReceipt</c> insert / update / no-op / failure.</item>
///   <item>Flip Status=Completed, persist counters, emit
///         <c>TREASURY_FEED.IMPORT_COMPLETED</c> Information audit row.</item>
/// </list>
/// </para>
/// <para>
/// <b>Idempotency.</b> Re-running for the same date is allowed when no
/// previously Completed run exists — the EF filtered unique index enforces
/// the rule at persist time. Row-level idempotency is honoured: an existing
/// receipt with identical content surfaces as
/// <c>TreasuryFeedImportRowStatus.Skipped</c>; a content drift surfaces as
/// <c>TreasuryFeedImportRowStatus.Updated</c>.
/// </para>
/// </remarks>
public interface ITreasuryFeedImporter
{
    /// <summary>Stable audit event code emitted on a successful import completion.</summary>
    public const string AuditImportCompleted = "TREASURY_FEED.IMPORT_COMPLETED";

    /// <summary>Stable audit event code emitted on an import failure (Critical severity).</summary>
    public const string AuditImportFailed = "TREASURY_FEED.IMPORT_FAILED";

    /// <summary>Stable failure code returned when the configured source rejects a fetch.</summary>
    public const string SourceNotConfiguredCode = "TREASURY_FEED.NOT_CONFIGURED";

    /// <summary>Stable failure code returned when the feed exceeds the per-file row cap.</summary>
    public const string TooManyRowsCode = "TREASURY_FEED.TOO_MANY_ROWS";

    /// <summary>Stable failure code returned when the parser cannot find a required header.</summary>
    public const string MissingHeaderCode = "TREASURY_FEED.MISSING_HEADER";

    /// <summary>
    /// Runs a single import attempt for <paramref name="feedDate"/>. The
    /// importer creates the registry row, advances its lifecycle, and
    /// returns the compact summary regardless of terminal status.
    /// </summary>
    /// <param name="feedDate">Calendar date the feed covers.</param>
    /// <param name="trigger">Origin of the run (Scheduled vs Manual).</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>
    /// On success the <see cref="TreasuryFeedImportSummaryDto"/> carrying the
    /// terminal status + counters; on early failure (e.g. source returned a
    /// configuration miss) a failed <see cref="Result{T}"/> with a stable
    /// error code.
    /// </returns>
    Task<Result<TreasuryFeedImportSummaryDto>> ImportAsync(
        DateOnly feedDate,
        TreasuryFeedTriggerKind trigger,
        CancellationToken cancellationToken = default);
}
