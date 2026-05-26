using System.Collections.Generic;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Core.ValueObjects;

/// <summary>
/// R0939 / iter 136 — pinned, declarative transition matrix for the
/// <see cref="ApplicationStatus"/> lifecycle. Materialised as a static
/// <see cref="StatusTransitionTable{TStatus}"/> instance so every mutator (service,
/// job, bulk operation) can consult ONE source of truth instead of hand-rolling
/// per-call <c>if</c>-ladders.
/// </summary>
/// <remarks>
/// <para>
/// <b>Spec source.</b> The chain
/// <c>Înregistrată → ÎnAșteptareDocumente? → ÎnExaminare → SemnatăUtilizator →
/// SemnatăȘefulDirecției? → AprobatăȘefulCNAS / Returnată / Refuz / Terminată</c>
/// pins the canonical CNAS workflow. The enum-to-Romanian mapping is documented on
/// <see cref="ApplicationStatus"/>.
/// </para>
/// <para>
/// <b>Allowed edges.</b> The matrix encodes:
/// </para>
/// <list type="bullet">
///   <item><description><c>Draft → Submitted | Withdrawn</c> (citizen submits / abandons)</description></item>
///   <item><description><c>Submitted → RejectedIncomplete | UnderExamination | Rejected | Withdrawn</c></description></item>
///   <item><description><c>RejectedIncomplete → Submitted | Rejected | Withdrawn</c>
///     (citizen resubmits, 30-day auto-close to Rejected, or withdraws)</description></item>
///   <item><description><c>UnderExamination → PendingApproval | Rejected | Withdrawn</c>
///     (examiner signs / rejects / citizen withdraws)</description></item>
///   <item><description><c>PendingApproval → SignedByDirector | Approved | Returned | Rejected</c>
///     (2-level direct approval OR 3-level intermediate; returned for rework; rejected outright)</description></item>
///   <item><description><c>SignedByDirector → Approved | Returned | Rejected</c>
///     (Șeful CNAS final approval / sends back / refuses)</description></item>
///   <item><description><c>Returned → UnderExamination</c> (reviewer kicks the dossier back to re-examination)</description></item>
///   <item><description><c>Approved → Closed</c> (service rendered)</description></item>
///   <item><description>Terminal: <c>Closed, Rejected, Withdrawn</c> — every outgoing edge is denied</description></item>
/// </list>
/// <para>
/// <b>Withdrawal asymmetry.</b> Citizen withdrawal is allowed from every pre-terminal
/// state EXCEPT once the dossier has been signed by the examiner (PendingApproval and
/// later). At that point the decision is in motion through the CNAS approval chain and
/// the existing <c>WithdrawAsync</c> guard treats the dossier as locked. This matches
/// the legacy <c>if (Status is Closed/Approved/Rejected)</c> ladder previously hard-
/// coded in <c>ApplicationServiceImpl.WithdrawAsync</c>.
/// </para>
/// <para>
/// <b>Stability contract.</b> The matrix is a public API surface (the
/// <see cref="StatusTransitionTable{TStatus}.IllegalTransitionCode"/> rides on top
/// via the Application-layer guard surface, but the SHAPE of the matrix is itself the
/// stable contract). Adding a new edge is a non-breaking widening; removing an edge or
/// renaming a state is breaking and MUST trigger a versioned migration.
/// </para>
/// </remarks>
public static class ApplicationStatusTransitions
{
    /// <summary>
    /// The pinned <see cref="StatusTransitionTable{TStatus}"/> covering every legal
    /// edge in the <see cref="ApplicationStatus"/> lifecycle. See class summary for
    /// the matrix and the spec source.
    /// </summary>
    public static readonly StatusTransitionTable<ApplicationStatus> Table = Build();

    /// <summary>
    /// Builds the read-only transition map consumed by <see cref="Table"/>. Kept
    /// private so external callers cannot mutate the matrix at runtime.
    /// </summary>
    /// <returns>The pinned table instance.</returns>
    private static StatusTransitionTable<ApplicationStatus> Build()
    {
        var map = new Dictionary<ApplicationStatus, IReadOnlySet<ApplicationStatus>>
        {
            // Citizen-side: Draft is the only non-CNAS-touched origin.
            [ApplicationStatus.Draft] = new HashSet<ApplicationStatus>
            {
                ApplicationStatus.Submitted,
                ApplicationStatus.Withdrawn,
            },

            // Înregistrată (Submitted) — intake gate. The intake worker either advances
            // the dossier into examination, asks the citizen for more documents, hard-
            // rejects on a validation failure, or honours a citizen withdrawal.
            [ApplicationStatus.Submitted] = new HashSet<ApplicationStatus>
            {
                ApplicationStatus.RejectedIncomplete,
                ApplicationStatus.UnderExamination,
                ApplicationStatus.Rejected,
                ApplicationStatus.Withdrawn,
            },

            // ÎnAșteptareDocumente (RejectedIncomplete) — 30-day window for the citizen
            // to complete the dossier. Resubmission flips back to Submitted; the SLA job
            // auto-closes to Rejected after 30 days (R0934).
            [ApplicationStatus.RejectedIncomplete] = new HashSet<ApplicationStatus>
            {
                ApplicationStatus.Submitted,
                ApplicationStatus.Rejected,
                ApplicationStatus.Withdrawn,
            },

            // ÎnExaminare (UnderExamination) — examiner-owned. Three outgoing edges:
            // sign-off (PendingApproval), examiner-driven refusal (Rejected), and
            // citizen withdrawal (Withdrawn) — withdrawal is still allowed because the
            // dossier has not yet been signed by the CNAS staff.
            [ApplicationStatus.UnderExamination] = new HashSet<ApplicationStatus>
            {
                ApplicationStatus.PendingApproval,
                ApplicationStatus.Rejected,
                ApplicationStatus.Withdrawn,
            },

            // SemnatăUtilizator (PendingApproval) — examiner has signed. For a 2-level
            // service the next step is Approved (Șeful CNAS direct approval); for a
            // 3-level service it is SignedByDirector (intermediate). The reviewer may
            // also return the dossier or hard-reject. NOTE: withdrawal is forbidden
            // once the dossier is on the approval chain (matches the legacy
            // ApplicationServiceImpl.WithdrawAsync guard).
            [ApplicationStatus.PendingApproval] = new HashSet<ApplicationStatus>
            {
                ApplicationStatus.SignedByDirector,
                ApplicationStatus.Approved,
                ApplicationStatus.Returned,
                ApplicationStatus.Rejected,
            },

            // SemnatăȘefulDirecției (SignedByDirector) — 3-level intermediate. Șeful
            // CNAS final-approves, returns for rework, or refuses.
            [ApplicationStatus.SignedByDirector] = new HashSet<ApplicationStatus>
            {
                ApplicationStatus.Approved,
                ApplicationStatus.Returned,
                ApplicationStatus.Rejected,
            },

            // Returnată (Returned) — the only outgoing edge is back into examination,
            // so the examiner can re-draft the decision. Per the matrix this state
            // is NOT a terminal: it is a transient "send back" state.
            [ApplicationStatus.Returned] = new HashSet<ApplicationStatus>
            {
                ApplicationStatus.UnderExamination,
            },

            // AprobatăȘefulCNAS (Approved) — final approval recorded; the only next
            // step is to render the service and close out (Terminată).
            [ApplicationStatus.Approved] = new HashSet<ApplicationStatus>
            {
                ApplicationStatus.Closed,
            },

            // ─── Terminal states ───
            // Closed (Terminată), Rejected (Refuz), Withdrawn are all terminal — every
            // outgoing edge is denied. The dictionary maps each to an explicit empty
            // set so the StatusTransitionTable lookup returns the same denial verdict
            // regardless of whether the entry exists; making the entries explicit
            // documents the lifecycle exhaustively in one place.
            [ApplicationStatus.Closed] = new HashSet<ApplicationStatus>(),
            [ApplicationStatus.Rejected] = new HashSet<ApplicationStatus>(),
            [ApplicationStatus.Withdrawn] = new HashSet<ApplicationStatus>(),
        };

        return new StatusTransitionTable<ApplicationStatus>(map);
    }
}
