namespace Cnas.Ps.Core.Domain;

/// <summary>
/// Cerere — an application/request submitted by a Solicitant to receive a public service from CNAS.
/// TOR §2.3 #1. Aggregate root of the cerere → dosar → decizie workflow described in §2.5.1.
/// </summary>
public sealed class ServiceApplication : AuditableEntity, IExternalId
{
    /// <summary>FK to the Solicitant who submitted the application.</summary>
    public long SolicitantId { get; set; }

    /// <summary>Navigation to the submitting Solicitant.</summary>
    public Solicitant? Solicitant { get; set; }

    /// <summary>FK to the ServicePassport (UC15) describing which service was requested.</summary>
    /// <remarks>
    /// R0142 / CF 15.04 — points at the SPECIFIC version row of the passport that was
    /// current at submission time. When a şef-direcţie publishes a new passport version
    /// the FK is NOT repointed — it pins the application to its original definition for
    /// the entire lifecycle (no in-flight drift).
    /// </remarks>
    public long ServicePassportId { get; set; }

    /// <summary>
    /// R0142 / CF 15.04 — denormalised copy of the
    /// <see cref="ServicePassport.Version"/> the application was submitted under.
    /// Duplicates the information already implicit in <see cref="ServicePassportId"/>
    /// (which points at the specific version row) but materialises it as a cheap
    /// column for reporting and for rare cases where the FK target gets soft-deleted
    /// from the catalogue. Defaults to <c>1</c> for backward compatibility.
    /// </summary>
    public int PinnedServicePassportVersion { get; set; } = 1;

    /// <summary>
    /// R0129 / CF 15.04 — denormalised copy of the
    /// <see cref="WorkflowDefinition.Version"/> that was current for the passport's
    /// <see cref="ServicePassport.WorkflowCode"/> at submission time. The workflow
    /// engine resolves the JSON definition via (<c>WorkflowCode</c>, this version)
    /// when advancing an in-flight application — so a republish of the workflow does
    /// not re-shape the steps the citizen is mid-way through. Defaults to <c>1</c>
    /// for backward compatibility.
    /// </summary>
    public int PinnedWorkflowVersion { get; set; } = 1;

    /// <summary>Lifecycle state — see <see cref="ApplicationStatus"/>.</summary>
    public ApplicationStatus Status { get; set; } = ApplicationStatus.Draft;

    /// <summary>
    /// Serialised form payload (JSONB in Postgres). The shape is governed by the
    /// ServicePassport's form definition; storage is schema-flexible per FLEX 002.
    /// </summary>
    public string FormPayloadJson { get; set; } = "{}";

    /// <summary>
    /// Snapshot of the applicant's display data at submission time (immutable snapshot
    /// per CLAUDE.md cross-cutting principle).
    /// </summary>
    public string? SnapshotJson { get; set; }

    /// <summary>UTC timestamp of submission (Draft → Submitted transition).</summary>
    public DateTime? SubmittedAtUtc { get; set; }

    /// <summary>UTC timestamp of final closure (Closed / Rejected / Withdrawn).</summary>
    public DateTime? ClosedAtUtc { get; set; }

    /// <summary>FK to the opened Dossier, once it exists.</summary>
    public long? DossierId { get; set; }

    /// <summary>Public-facing reference number printed on confirmations and decisions.</summary>
    public string? ReferenceNumber { get; set; }

    /// <summary>
    /// UTC timestamp at which the approved benefit payment was successfully dispatched to MPay.
    /// Used by <c>MPayDispatcherJob</c> to guarantee idempotency — once stamped, the row is
    /// skipped on subsequent runs so a beneficiary cannot be paid twice for the same dossier.
    /// </summary>
    public DateTime? PaymentDispatchedAtUtc { get; set; }

    /// <summary>
    /// Upstream MPay transaction id captured on a successful dispatch. Stored for cross-system
    /// reconciliation and for audit traceability (TOR SEC 042 — trasabilitate).
    /// </summary>
    public string? PaymentTransactionId { get; set; }

    /// <summary>
    /// Last-known status string echoed back by MPay (e.g. <c>ACCEPTED</c>, <c>SETTLED</c>).
    /// Free-form because the upstream protocol leaves the value open; never interpreted
    /// by business logic.
    /// </summary>
    public string? PaymentStatus { get; set; }

    /// <summary>
    /// UTC timestamp when the application most recently entered <see cref="ApplicationStatus.RejectedIncomplete"/>.
    /// Null when the application is not currently in that state. Drives the 30-day missing-docs auto-close
    /// SLA enforced by <c>MissingDocsSlaJob</c> (R0934).
    /// </summary>
    /// <remarks>
    /// Writers MUST keep this field in sync with <see cref="Status"/>. The domain helper
    /// <see cref="TransitionStatus(ApplicationStatus, System.DateTime)"/> enforces the invariant:
    /// transitions INTO <see cref="ApplicationStatus.RejectedIncomplete"/> stamp the current UTC instant;
    /// transitions OUT (back to <see cref="ApplicationStatus.Submitted"/> on resubmission, or to any
    /// terminal status) clear the stamp.
    /// </remarks>
    public DateTime? RejectedIncompleteSinceUtc { get; set; }

    /// <summary>
    /// Transitions <see cref="Status"/> to <paramref name="next"/> while preserving the
    /// <see cref="RejectedIncompleteSinceUtc"/> invariant: the timestamp is stamped on entry
    /// into <see cref="ApplicationStatus.RejectedIncomplete"/> and cleared on any exit.
    /// </summary>
    /// <param name="next">Target lifecycle state.</param>
    /// <param name="nowUtc">Current UTC instant supplied by the caller (use <c>ICnasTimeProvider.UtcNow</c>).</param>
    /// <remarks>
    /// Centralising the bookkeeping here means a future writer that adds a new transition cannot
    /// drift out of sync with the SLA job's filter — every Status change MUST go through this method.
    /// The auto-close job itself bypasses this helper (it clears the field inline alongside the
    /// terminal status flip) because it is the single authorised exit from the timed-out branch.
    /// </remarks>
    public void TransitionStatus(ApplicationStatus next, DateTime nowUtc)
    {
        if (next == ApplicationStatus.RejectedIncomplete)
        {
            // Re-entry into RejectedIncomplete (e.g. citizen resubmitted but documents still missing)
            // re-stamps the clock so the 30-day window restarts from the latest event.
            RejectedIncompleteSinceUtc = nowUtc;
        }
        else if (Status == ApplicationStatus.RejectedIncomplete)
        {
            RejectedIncompleteSinceUtc = null;
        }
        Status = next;
    }

    /// <summary>
    /// R0671 / TOR CF 18.06 — stable code of the CNAS subdivision (<see cref="CnasBranch.Code"/>)
    /// currently handling this application. Used by the access-scope filter so a staff user can
    /// see only applications assigned to their assigned subdivision(s). <c>null</c> on rows that
    /// pre-date scoping or on applications that have not yet been routed to a specific branch
    /// (visible to every scoped caller per the
    /// <c>Cnas.Ps.Application.Abstractions.IAccessScope</c> NULL-data semantics — cref is a
    /// plain string because Core may not reference Application).
    /// Capped at 64 chars at the persistence layer (matches the <see cref="CnasBranch.Code"/> cap).
    /// </summary>
    public string? SubdivisionCode { get; set; }

    /// <summary>
    /// R0570 / TOR CF 08.02 — internal user id of the examiner selected by
    /// <c>IExaminerAssignmentService.AssignExaminerAsync</c> at submission
    /// time. Distinct from <see cref="Dossier.AssignedExaminerId"/>: this
    /// column captures the round-robin pick BEFORE the dossier exists, so
    /// the assignment is recoverable for audit even when the application
    /// stays in <see cref="ApplicationStatus.Submitted"/> awaiting intake
    /// validation. <c>null</c> on rows that pre-date the assignment service.
    /// </summary>
    public long? AssignedExaminerUserId { get; set; }
}
