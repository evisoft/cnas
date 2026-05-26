using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Application.BulkActions.Operations;

/// <summary>
/// R0305 / BP 1.8 / TOR Annex 1 — bulk operation that reassigns every selected
/// <see cref="Contributor"/> to a different <see cref="CnasBranch"/>.
/// Parameter shape: <c>{ "newBranchCode": "CNAS-CHIS-CTR" }</c>. Verifies the
/// supplied branch code exists in the live <c>CnasBranches</c> table on every
/// row (cheap query keyed on the natural code, bounded by
/// <see cref="MaxRowsPerRun"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Audit emission.</b> Emits a <c>CONTRIBUTOR.BRANCH_REASSIGNED</c> audit row
/// (severity Notice) per successfully reassigned contributor. The bulk runner
/// wraps the per-row audits with its own STARTED/COMPLETED rows so the operator
/// dashboard captures both the per-row trail and the run-level summary.
/// </para>
/// <para>
/// <b>Failure modes.</b>
/// <list type="bullet">
///   <item><description><c>VALIDATION_FAILED</c> — the parameters JSON is malformed
///   or missing <c>newBranchCode</c>.</description></item>
///   <item><description><c>NOT_FOUND</c> — the row no longer exists or is soft-deleted.</description></item>
///   <item><description><c>BRANCH_NOT_FOUND</c> — the supplied branch code is not
///   registered in the <c>CnasBranches</c> table.</description></item>
///   <item><description><c>ALREADY_AT_BRANCH</c> — the contributor is already
///   assigned to the requested branch (no-op).</description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class ContributorBulkReassignBranchOperation : IBulkOperation
{
    private readonly ICnasDbContext _db;
    private readonly ICnasTimeProvider _clock;
    private readonly IAuditService _audit;

    /// <summary>Creates the operation.</summary>
    /// <param name="db">Per-request CNAS DbContext.</param>
    /// <param name="clock">UTC clock used to stamp <c>UpdatedAtUtc</c> + audit timestamps.</param>
    /// <param name="audit">Audit-service façade used for per-row CONTRIBUTOR.BRANCH_REASSIGNED rows.</param>
    public ContributorBulkReassignBranchOperation(
        ICnasDbContext db,
        ICnasTimeProvider clock,
        IAuditService audit)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(audit);

        _db = db;
        _clock = clock;
        _audit = audit;
    }

    /// <summary>Stable operation code (part of the public dispatch contract).</summary>
    public const string OperationCode = "Contributor.ReassignBranch";

    /// <summary>
    /// Cached <see cref="JsonSerializerOptions"/> reused on every row to satisfy
    /// the CA1869 analyzer guidance — building a fresh options instance per
    /// deserialise call is unnecessary when the shape never changes.
    /// </summary>
    private static readonly JsonSerializerOptions CachedJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <inheritdoc />
    public string Code => OperationCode;

    /// <inheritdoc />
    public string Registry => BulkRegistries.Contributor;

    /// <inheritdoc />
    public string RequiredPermission => "Contributor.Manage";

    /// <inheritdoc />
    public int MaxRowsPerRun => 1_000;

    /// <inheritdoc />
    public bool RequiresParameters => true;

    /// <inheritdoc />
    public async Task<BulkRowOutcome> ExecuteAsync(
        long rowId,
        string? parametersJson,
        ICallerContext caller,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);

        // Re-parse the parameters per row — cheap, keeps the operation stateless, and
        // means a bad shape surfaces as a per-row failure rather than aborting the
        // whole run.
        Parameters? parameters;
        try
        {
            parameters = JsonSerializer.Deserialize<Parameters>(
                parametersJson ?? "null",
                CachedJsonOptions);
        }
        catch (JsonException ex)
        {
            return BulkRowOutcome.Failed(ErrorCodes.ValidationFailed, $"Parameters JSON malformed: {ex.Message}");
        }
        if (parameters is null || string.IsNullOrWhiteSpace(parameters.NewBranchCode))
        {
            return BulkRowOutcome.Failed(
                ErrorCodes.ValidationFailed,
                "Parameters must carry a non-empty newBranchCode.");
        }
        var newBranchCode = parameters.NewBranchCode;

        // Verify the branch exists. The CnasBranches table is small (≤ a few dozen rows)
        // so the per-row lookup is effectively free; we deliberately do NOT cache the set
        // because the runner processes rows serially and a fresh DbContext per scope
        // already provides invalidation.
        var branchExists = await _db.CnasBranches
            .AnyAsync(b => b.Code == newBranchCode && b.IsActive, ct)
            .ConfigureAwait(false);
        if (!branchExists)
        {
            return BulkRowOutcome.Failed(
                "BRANCH_NOT_FOUND",
                $"CNAS branch '{newBranchCode}' not found.");
        }

        var contributor = await _db.Contributors
            .SingleOrDefaultAsync(c => c.Id == rowId && c.IsActive, ct)
            .ConfigureAwait(false);
        if (contributor is null)
        {
            return BulkRowOutcome.Failed(ErrorCodes.NotFound, "Contributor not found.");
        }

        // No-op: already at the requested branch. Surface as a failure outcome so the
        // run summary distinguishes "nothing to do" from "succeeded by reassignment".
        // The stable code "ALREADY_AT_BRANCH" lets the UI render a friendly hint.
        if (string.Equals(contributor.CnasBranchCode, newBranchCode, StringComparison.Ordinal))
        {
            return BulkRowOutcome.Failed(
                "ALREADY_AT_BRANCH",
                $"Contributor is already assigned to branch '{newBranchCode}'.");
        }

        var now = _clock.UtcNow;
        var previousBranchCode = contributor.CnasBranchCode;
        contributor.CnasBranchCode = newBranchCode;
        contributor.UpdatedAtUtc = now;
        contributor.UpdatedBy = caller.UserSqid;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var detailsJson = JsonSerializer.Serialize(
            new
            {
                previousBranchCode,
                newBranchCode,
            },
            CachedJsonOptions);
        await _audit.RecordAsync(
            eventCode: "CONTRIBUTOR.BRANCH_REASSIGNED",
            severity: AuditSeverity.Notice,
            actorId: caller.UserSqid ?? "system",
            targetEntity: nameof(Contributor),
            targetEntityId: contributor.Id,
            detailsJson: detailsJson,
            sourceIp: caller.SourceIp,
            correlationId: caller.CorrelationId,
            cancellationToken: ct).ConfigureAwait(false);

        return BulkRowOutcome.Succeeded();
    }

    /// <summary>
    /// JSON-deserialisable shape of the operation's parameters payload. Internal —
    /// callers post the JSON directly; this type only exists to bind it.
    /// </summary>
    /// <param name="NewBranchCode">Natural code of the destination CNAS branch (e.g. <c>"CNAS-CHIS-CTR"</c>).</param>
    private sealed record Parameters(string? NewBranchCode);
}
