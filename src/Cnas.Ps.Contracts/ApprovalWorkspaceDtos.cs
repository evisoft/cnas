namespace Cnas.Ps.Contracts;

/// <summary>
/// R0590 / TOR CF 10.01 — summary chip-strip projection rendered at the top of the
/// approval-workspace UI. Three integer counters describe the size and urgency of
/// the decider's queue at a single glance: total pending decisions, the subset
/// whose SLA deadline has already lapsed, and the subset emitted today (UTC).
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope.</b> All three counts honour the caller's row-level access scope
/// (R0671 / TOR CF 18.06) — a regional director only sees dossiers under their
/// subdivision allow-list. The "today" boundary is computed against
/// <c>ICnasTimeProvider.UtcNow.Date</c> so the chip resets at midnight UTC.
/// </para>
/// <para>
/// <b>Wire shape.</b> Plain integers, no nullable fields, no Sqids — this is a
/// non-PII aggregate. The counters can reach zero (empty queue) but never go
/// negative.
/// </para>
/// </remarks>
/// <param name="PendingCount">Total number of dossiers currently awaiting the caller's approval.</param>
/// <param name="OverdueCount">Subset of <paramref name="PendingCount"/> whose SLA deadline has lapsed.</param>
/// <param name="TodayCount">Subset of <paramref name="PendingCount"/> that landed on the queue today (UTC).</param>
public sealed record ApprovalWorkspaceSummaryDto(
    int PendingCount,
    int OverdueCount,
    int TodayCount);

/// <summary>
/// R0590 / TOR CF 10.01 — a single row inside the approval-workspace list. Pinned
/// to the dossier (the canonical "decision unit" in the CNAS workflow) and carries
/// the snapshot fields the decider needs to triage at a glance: dossier number,
/// decision title (from the service-passport name), originating examiner, the
/// instant the dossier was forwarded for approval, and the SLA deadline.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sqid invariant.</b> Every id on the wire is Sqid-encoded (CLAUDE.md RULE 3).
/// The dossier id is the primary anchor; the examiner sqid is included so the UI
/// can deep-link to the examiner's profile or filter rows by examiner.
/// </para>
/// <para>
/// <b>Sort order.</b> The service returns rows in (SlaDeadlineUtc ascending, then
/// EmittedAtUtc ascending) order — the most-urgent decisions float to the top of
/// the list naturally, matching the chip-strip's "Overdue" semantics.
/// </para>
/// </remarks>
/// <param name="Id">Sqid-encoded dossier identifier — the URL anchor for Approve / Reject.</param>
/// <param name="DossierCode">Public-facing dossier number (e.g. <c>D-2026-0001A2B3</c>).</param>
/// <param name="DecisionTitle">Human-readable name of the service the decision pertains to.</param>
/// <param name="ExaminerName">Display name of the examiner who forwarded the dossier; null when unassigned.</param>
/// <param name="ExaminerSqid">Sqid id of the examiner; null when unassigned.</param>
/// <param name="EmittedAtUtc">UTC instant at which the dossier landed on the approval queue.</param>
/// <param name="SlaDeadlineUtc">UTC deadline for the approval decision; null when the row has no SLA.</param>
public sealed record ApprovalQueueItemDto(
    string Id,
    string DossierCode,
    string DecisionTitle,
    string? ExaminerName,
    string? ExaminerSqid,
    DateTime EmittedAtUtc,
    DateTime? SlaDeadlineUtc);
