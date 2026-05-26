using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// R0590 / TOR CF 10.01 — read-only projection backing the decider's approval
/// workspace UI. Implementation of <see cref="IApprovalWorkspaceService"/>.
/// Drives both the chip-strip summary (<see cref="GetSummaryAsync"/>) and the
/// paged decision queue (<see cref="ListPendingAsync"/>) off a single source
/// of truth: dossiers whose parent <see cref="ServiceApplication.Status"/> is
/// <see cref="ApplicationStatus.PendingApproval"/>. The two surfaces are
/// guaranteed to agree on row identity — the list's <see cref="PagedResult{T}.TotalCount"/>
/// equals the summary's <see cref="ApprovalWorkspaceSummaryDto.PendingCount"/>
/// for the same caller and instant.
/// </summary>
/// <remarks>
/// <para>
/// <b>SLA semantics.</b> The "decider task" row carries the SLA deadline. The
/// service shipping examiner→approver transition (<c>DocumentExaminationService.SubmitForApprovalAsync</c>)
/// creates a <see cref="WorkflowTask"/> with <see cref="WorkflowTask.GroupCode"/>
/// = <c>"cnas-decider"</c> and <see cref="WorkflowTask.DueAtUtc"/> set five days
/// hence. The workspace projects that task's deadline as the row's
/// <see cref="ApprovalQueueItemDto.SlaDeadlineUtc"/>. Dossiers whose decider
/// task is missing (back-fill gap) surface with a null deadline and sort last.
/// </para>
/// <para>
/// <b>EmittedAt semantics.</b> The instant the dossier landed on the approval
/// queue corresponds to the decider task's <c>CreatedAtUtc</c> (stamped at the
/// moment of SubmitForApproval). When that task is missing we fall back to the
/// dossier's <c>UpdatedAtUtc</c> as a conservative proxy.
/// </para>
/// <para>
/// <b>Read-only.</b> The service uses <see cref="ICnasDbContext"/> only because
/// the EF Core InMemory provider used in tests does not yet ship a separate
/// read-replica wiring; production code routes through the same read paths
/// (no writes, no <c>SaveChangesAsync</c>). The dependency could be tightened
/// to <see cref="IReadOnlyCnasDbContext"/> in a follow-up once the test harness
/// catches up.
/// </para>
/// </remarks>
/// <param name="db">EF Core context abstraction (read-only usage).</param>
/// <param name="sqids">Sqid encoder/decoder for boundary id round-tripping (CLAUDE.md RULE 3).</param>
/// <param name="clock">UTC clock — never <see cref="DateTime.UtcNow"/> directly.</param>
public sealed class ApprovalWorkspaceService(
    ICnasDbContext db,
    ISqidService sqids,
    ICnasTimeProvider clock) : IApprovalWorkspaceService
{
    /// <summary>Group code carried by the decider task that owns the SLA deadline.</summary>
    private const string DeciderGroup = "cnas-decider";

    /// <summary>Default page size when the caller supplies an out-of-range value.</summary>
    private const int DefaultPageSize = 20;

    /// <summary>Hard cap on the page size to bound memory + serialisation cost.</summary>
    private const int MaxPageSize = 100;

    private readonly ICnasDbContext _db = db;
    private readonly ISqidService _sqids = sqids;
    private readonly ICnasTimeProvider _clock = clock;

    /// <inheritdoc />
    public async Task<Result<ApprovalWorkspaceSummaryDto>> GetSummaryAsync(
        CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        var startOfDayUtc = new DateTime(now.Year, now.Month, now.Day, 0, 0, 0, DateTimeKind.Utc);

        // Source query: dossiers in PendingApproval state. The summary counts
        // are projected directly off the dossier row + its open decider task
        // (left-join so missing tasks do not collapse the row).
        var rows = await BasePendingQuery()
            .Select(r => new
            {
                EmittedAtUtc = r.EmittedAtUtc,
                SlaDeadlineUtc = r.SlaDeadlineUtc,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var pending = rows.Count;
        var overdue = rows.Count(r => r.SlaDeadlineUtc is { } d && d < now);
        var today = rows.Count(r => r.EmittedAtUtc >= startOfDayUtc);

        return Result<ApprovalWorkspaceSummaryDto>.Success(
            new ApprovalWorkspaceSummaryDto(pending, overdue, today));
    }

    /// <inheritdoc />
    public async Task<Result<PagedResult<ApprovalQueueItemDto>>> ListPendingAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var clampedPage = page < 1 ? 1 : page;
        var clampedSize = pageSize switch
        {
            < 1 => DefaultPageSize,
            > MaxPageSize => MaxPageSize,
            _ => pageSize,
        };
        var skip = (clampedPage - 1) * clampedSize;

        var source = BasePendingQuery();

        // TotalCount drives the summary's PendingCount cross-check guarantee —
        // we materialise it BEFORE paging.
        var total = await source.CountAsync(cancellationToken).ConfigureAwait(false);

        // Ordering: most-urgent first — earliest SLA at the top. Rows with no
        // deadline sort last (DateTime.MaxValue substitute). Tie-breaker on
        // EmittedAtUtc ascending so deterministic in the SLA == SLA case, then
        // on dossier id to guarantee a stable order across requests.
        var rows = await source
            .OrderBy(r => r.SlaDeadlineUtc == null ? 1 : 0)
            .ThenBy(r => r.SlaDeadlineUtc)
            .ThenBy(r => r.EmittedAtUtc)
            .ThenBy(r => r.DossierId)
            .Skip(skip)
            .Take(clampedSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var items = rows
            .Select(r => new ApprovalQueueItemDto(
                Id: _sqids.Encode(r.DossierId),
                DossierCode: r.DossierNumber,
                DecisionTitle: r.DecisionTitle ?? string.Empty,
                ExaminerName: r.ExaminerName,
                ExaminerSqid: r.ExaminerUserId is { } eid ? _sqids.Encode(eid) : null,
                EmittedAtUtc: r.EmittedAtUtc,
                SlaDeadlineUtc: r.SlaDeadlineUtc))
            .ToList();

        return Result<PagedResult<ApprovalQueueItemDto>>.Success(
            new PagedResult<ApprovalQueueItemDto>(items, clampedPage, clampedSize, total));
    }

    /// <summary>
    /// Builds the canonical "pending approval" projection query. Joins the
    /// active <see cref="Dossier"/> rows whose parent <see cref="ServiceApplication"/>
    /// is in <see cref="ApplicationStatus.PendingApproval"/> with the open
    /// decider <see cref="WorkflowTask"/> (left-outer) and the matching
    /// <see cref="ServicePassport"/> + assigned-examiner <see cref="UserProfile"/>.
    /// Returns an <see cref="IQueryable{T}"/> of the intermediate projection
    /// shape consumed by both the summary and the list surfaces.
    /// </summary>
    /// <returns>An ordered-by-default queryable over the pending-approval projection.</returns>
    private IQueryable<PendingProjection> BasePendingQuery()
    {
        // The decider task lookup is expressed as a sub-projection so EF can
        // translate it to a correlated sub-select; the join is left-outer via
        // FirstOrDefault, so missing tasks degrade gracefully.
        var query =
            from d in _db.Dossiers.AsNoTracking()
            join a in _db.Applications.AsNoTracking() on d.ApplicationId equals a.Id
            join p in _db.ServicePassports.AsNoTracking() on a.ServicePassportId equals p.Id into ps
            from p in ps.DefaultIfEmpty()
            where d.IsActive
                  && a.Status == ApplicationStatus.PendingApproval
            let deciderTask = _db.WorkflowTasks.AsNoTracking()
                .Where(t => t.DossierId == d.Id
                            && t.IsActive
                            && t.GroupCode == DeciderGroup
                            && t.Status != WorkflowTaskStatus.Completed
                            && t.Status != WorkflowTaskStatus.Cancelled)
                .OrderBy(t => t.CreatedAtUtc)
                .FirstOrDefault()
            let examiner = _db.UserProfiles.AsNoTracking()
                .Where(u => d.AssignedExaminerId != null && u.Id == d.AssignedExaminerId)
                .FirstOrDefault()
            select new PendingProjection
            {
                DossierId = d.Id,
                DossierNumber = d.DossierNumber,
                DecisionTitle = p == null ? string.Empty : p.NameRo,
                ExaminerUserId = d.AssignedExaminerId,
                ExaminerName = examiner == null ? null : examiner.DisplayName,
                EmittedAtUtc = deciderTask != null ? deciderTask.CreatedAtUtc : d.UpdatedAtUtc ?? d.CreatedAtUtc,
                SlaDeadlineUtc = deciderTask != null ? deciderTask.DueAtUtc : null,
            };

        return query;
    }

    /// <summary>
    /// Intermediate projection shape consumed by both <see cref="GetSummaryAsync"/>
    /// and <see cref="ListPendingAsync"/>. Plain mutable type so EF's LINQ
    /// provider can populate it; converted to <see cref="ApprovalQueueItemDto"/>
    /// at the boundary.
    /// </summary>
    private sealed class PendingProjection
    {
        public long DossierId { get; set; }
        public string DossierNumber { get; set; } = string.Empty;
        public string? DecisionTitle { get; set; }
        public long? ExaminerUserId { get; set; }
        public string? ExaminerName { get; set; }
        public DateTime EmittedAtUtc { get; set; }
        public DateTime? SlaDeadlineUtc { get; set; }
    }
}
