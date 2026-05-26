using Cnas.Ps.Application.Audit;
using Cnas.Ps.Application.Integrity;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Services.Integrity.Checks;

/// <summary>
/// R2709 / TOR SEC 053 — invariant check that the audit-log SHA-256 hash
/// chain (R0194 / SEC 047) is intact. Wraps the standalone
/// <see cref="IAuditChainVerifier"/> so the chain integrity signal flows
/// through the same operator surface as every other invariant: the
/// <c>IntegrityCheckRun</c> dashboard and the <c>IntegrityCheckFinding</c>
/// acknowledgement workflow.
/// </summary>
/// <remarks>
/// <para>
/// The verifier walks rows in <c>AuditLog.Id</c> order and stops at the first
/// break, reporting <c>FirstBrokenRowId</c> and a stable
/// <c>FirstBrokenReason</c> code (<c>PrevHashMismatch</c> or
/// <c>RowHashMismatch</c>). Because the verifier short-circuits on the first
/// break, this check emits at most ONE finding per run — that's intentional:
/// the chain is a strict sequence, so subsequent rows cannot be evaluated
/// independently of the first break.
/// </para>
/// <para>
/// <b>PII discipline.</b> The finding description carries only the stable
/// break-kind code; never the row's <c>DetailsJson</c>, <c>ActorId</c>, or any
/// other audit-row content. Operators investigating the finding navigate to
/// the row by its <c>AggregateRowId</c> through the existing audit-log viewer
/// (where the standard masking already applies).
/// </para>
/// <para>
/// <b>Technical failure path.</b> When the verifier itself errors (e.g. the
/// streaming replica is unreachable), the check still surfaces a single
/// Critical finding so the operator sees the failure on the integrity-check
/// dashboard. <c>AggregateRowId</c> is set to <c>0</c> in that branch — there
/// is no specific broken row to point at — and the description carries the
/// failure error code (no PII).
/// </para>
/// </remarks>
public sealed class AuditChainIntegrityCheck : IIntegrityCheck
{
    private readonly IAuditChainVerifier _verifier;

    /// <summary>Constructs the check with its verifier dependency.</summary>
    /// <param name="verifier">R0194 hash-chain verifier.</param>
    public AuditChainIntegrityCheck(IAuditChainVerifier verifier)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        _verifier = verifier;
    }

    /// <inheritdoc />
    public string CheckCode => "AUDIT_LOG.CHAIN";

    /// <inheritdoc />
    public string AggregateName => nameof(AuditLog);

    /// <inheritdoc />
    public IntegrityFindingSeverity Severity => IntegrityFindingSeverity.Critical;

    /// <inheritdoc />
    public async Task<IntegrityCheckPartialResult> RunAsync(
        IIntegrityCheckContext ctx,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        // Invoke the verifier through its own contract — it owns the algorithm
        // (canonical-form recipe + GENESIS anchor + walk order). This check
        // exists purely to map the verifier's report shape onto the integrity
        // sweep's finding shape.
        var verifyResult = await _verifier
            .VerifyAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!verifyResult.IsSuccess)
        {
            // Technical failure (e.g. reader unreachable). Surface ONE
            // Critical finding so the operator dashboard reflects the failure.
            // AggregateRowId=0 because no specific row was identified; the
            // description carries the stable error code only — no PII.
            return new IntegrityCheckPartialResult(
                RowsScanned: 0,
                Findings: new List<IntegrityCheckFindingRecord>
                {
                    new(
                        CheckCode: CheckCode,
                        Severity: Severity,
                        AggregateName: AggregateName,
                        AggregateRowId: 0L,
                        Description: $"Audit chain verification failed technically: {verifyResult.ErrorCode ?? "UNKNOWN"}",
                        ExpectedValue: null,
                        ActualValue: null),
                });
        }

        var report = verifyResult.Value;
        if (report.IsValid)
        {
            // Happy path: chain is intact. RowsScanned reflects how many rows
            // the verifier walked; no findings emitted.
            return new IntegrityCheckPartialResult(
                RowsScanned: report.CheckedCount,
                Findings: Array.Empty<IntegrityCheckFindingRecord>());
        }

        // Broken-chain path. The verifier short-circuits on the first break;
        // we emit exactly one Critical finding. Description carries the
        // stable reason code (PrevHashMismatch | RowHashMismatch). No PII —
        // operators correlate the row by AggregateRowId through the existing
        // audit-log viewer.
        var brokenRowId = report.FirstBrokenRowId ?? 0L;
        var reason = report.FirstBrokenReason ?? "Unknown";
        return new IntegrityCheckPartialResult(
            RowsScanned: report.CheckedCount,
            Findings: new List<IntegrityCheckFindingRecord>
            {
                new(
                    CheckCode: CheckCode,
                    Severity: Severity,
                    AggregateName: AggregateName,
                    AggregateRowId: brokenRowId,
                    Description: $"Audit chain integrity broken: {reason}",
                    ExpectedValue: null,
                    ActualValue: null),
            });
    }
}
