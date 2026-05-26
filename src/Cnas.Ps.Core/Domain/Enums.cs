namespace Cnas.Ps.Core.Domain;

/// <summary>
/// Lifecycle state of a <see cref="ServiceApplication"/> (Cerere) as it travels through TOR §2.5.1.
/// </summary>
/// <remarks>
/// <para>
/// R0939 / iter 136 — the 8-state CNAS spec lifecycle is documented as the Romanian
/// chain <c>Înregistrată → ÎnAșteptareDocumente? → ÎnExaminare → SemnatăUtilizator →
/// SemnatăȘefulDirecției? → AprobatăȘefulCNAS / Returnată / Refuz / Terminată</c>. The
/// enum values map onto that lifecycle as follows:
/// </para>
/// <list type="bullet">
///   <item><description>Înregistrată → <see cref="Submitted"/></description></item>
///   <item><description>ÎnAșteptareDocumente → <see cref="RejectedIncomplete"/></description></item>
///   <item><description>ÎnExaminare → <see cref="UnderExamination"/></description></item>
///   <item><description>SemnatăUtilizator → <see cref="PendingApproval"/></description></item>
///   <item><description>SemnatăȘefulDirecției → <see cref="SignedByDirector"/></description></item>
///   <item><description>AprobatăȘefulCNAS → <see cref="Approved"/></description></item>
///   <item><description>Returnată → <see cref="Returned"/></description></item>
///   <item><description>Refuz → <see cref="Rejected"/></description></item>
///   <item><description>Terminată → <see cref="Closed"/></description></item>
/// </list>
/// <para>
/// The complete legal-transition matrix lives on
/// <see cref="Cnas.Ps.Core.ValueObjects.ApplicationStatusTransitions.Table"/>; mutators
/// MUST consult that table (typically via the Application-layer
/// <c>IApplicationStatusGuard</c>) before flipping <see cref="ServiceApplication.Status"/>.
/// </para>
/// </remarks>
public enum ApplicationStatus
{
    /// <summary>Draft saved by the applicant; not yet submitted.</summary>
    Draft = 0,

    /// <summary>Submitted by the applicant and pending intake validation by CNAS staff (Înregistrată).</summary>
    Submitted = 1,

    /// <summary>Documents incomplete — applicant must complete and resubmit (ÎnAșteptareDocumente, TOR §2.5.1).</summary>
    RejectedIncomplete = 2,

    /// <summary>Routed to a CNAS examiner; dossier opened (ÎnExaminare).</summary>
    UnderExamination = 3,

    /// <summary>Decision drafted and signed by the CNAS examiner; awaiting șef-direcție review (SemnatăUtilizator).</summary>
    PendingApproval = 4,

    /// <summary>Approved by șef-direcție / Șeful CNAS; decision document being prepared (AprobatăȘefulCNAS).</summary>
    Approved = 5,

    /// <summary>Rejected by examiner / decider. Reason recorded on the decision (Refuz).</summary>
    Rejected = 6,

    /// <summary>Final decision delivered to applicant; service rendered (Terminată).</summary>
    Closed = 7,

    /// <summary>Withdrawn by the applicant before final decision.</summary>
    Withdrawn = 8,

    /// <summary>
    /// R0939 — 3-level approval intermediate state. The decision draft has been
    /// counter-signed by the șef-direcție (department head) and is awaiting the Șeful
    /// CNAS (final approval). Reached from <see cref="PendingApproval"/> on services
    /// configured for 3-level approval depth (see R0936). Maps to the Romanian
    /// lifecycle label "Semnată Șeful Direcției".
    /// </summary>
    SignedByDirector = 9,

    /// <summary>
    /// R0939 — the decision was rejected by a reviewer (examiner / șef-direcție / Șeful
    /// CNAS) and returned to the previous reviewer for rework. Distinct from
    /// <see cref="Rejected"/> (which is a terminal refusal of the citizen's request).
    /// Maps to the Romanian lifecycle label "Returnată". From <see cref="Returned"/> the
    /// dossier flows BACK to <see cref="UnderExamination"/> for re-drafting.
    /// </summary>
    Returned = 10,
}

/// <summary>Classification of a Solicitant (applicant) per TOR §2.2.</summary>
public enum ApplicantKind
{
    /// <summary>Natural person (persoană fizică).</summary>
    NaturalPerson = 0,

    /// <summary>Legal person (persoană juridică).</summary>
    LegalPerson = 1,
}

/// <summary>
/// Document kind issued by CNAS or attached to a Dossier per TOR §2.3 #3.
/// </summary>
public enum DocumentKind
{
    /// <summary>Generic citizen-supplied attachment (PDF, scan, photo).</summary>
    Attachment = 0,

    /// <summary>Decision document (decizia) emitted by CNAS.</summary>
    Decision = 10,

    /// <summary>Certificate (certificat) emitted by CNAS.</summary>
    Certificate = 20,

    /// <summary>Account statement (extras) emitted by CNAS.</summary>
    Extract = 30,

    /// <summary>Informational document.</summary>
    Information = 40,

    /// <summary>Internal note (notă internă) — not user-visible.</summary>
    InternalNote = 50,
}

/// <summary>State of a single workflow Task (Sarcină) per TOR §2.3 #9.</summary>
public enum WorkflowTaskStatus
{
    /// <summary>Pending — not yet picked up.</summary>
    Pending = 0,

    /// <summary>In progress — assignee is working on it.</summary>
    InProgress = 1,

    /// <summary>Completed successfully.</summary>
    Completed = 2,

    /// <summary>Cancelled (workflow change, withdrawn application, ...).</summary>
    Cancelled = 3,

    /// <summary>Overdue — SLA breach; surfaces in reports per UI 015.</summary>
    Overdue = 4,
}

/// <summary>Severity classification used by the audit log (TOR SEC 038–048).</summary>
public enum AuditSeverity
{
    /// <summary>Informational record — read access, lookups.</summary>
    Information = 0,

    /// <summary>Notice — write access to non-sensitive data.</summary>
    Notice = 1,

    /// <summary>Sensitive — access to confidential data, role/permission change.</summary>
    Sensitive = 2,

    /// <summary>Critical — security-relevant change; also mirrored to MLog per SEC 056.</summary>
    Critical = 3,
}

/// <summary>Channel used to deliver a notification to a recipient (TOR §2.5.1 + MNotify).</summary>
public enum NotificationChannel
{
    /// <summary>Web inbox inside SI PS (visible after login).</summary>
    InApp = 0,

    /// <summary>Email — dispatched via MNotify.</summary>
    Email = 1,

    /// <summary>SMS — dispatched via MNotify.</summary>
    Sms = 2,
}

/// <summary>
/// Lifecycle status of an outbound <see cref="Notification"/>. Drives the Annex 6g
/// delivery-stats report and any future delivery-retry workflow.
/// </summary>
public enum NotificationDeliveryStatus
{
    /// <summary>Created but not yet attempted by the dispatcher.</summary>
    Pending = 0,

    /// <summary>Dispatched successfully (the channel adapter returned OK).</summary>
    Delivered = 1,

    /// <summary>The dispatcher attempted delivery but the channel adapter failed.</summary>
    Failed = 2,

    /// <summary>The dispatcher refused to attempt delivery because of a policy
    /// (recipient opt-out, quiet hours, suppression list, etc.).</summary>
    Suppressed = 3,
}

/// <summary>
/// Lifecycle state of a <see cref="PendingAdminAction"/> in the maker-checker workflow
/// (R0058 / SEC 027). Transitions:
/// <c>Pending → Approved | Rejected | Expired</c>; finalised statuses are terminal.
/// </summary>
public enum PendingAdminActionStatus
{
    /// <summary>Submitted by the maker; waiting for a second administrator to decide.</summary>
    Pending = 0,

    /// <summary>Approved by the checker; the executor has run (or will be retried until it does).</summary>
    Approved = 1,

    /// <summary>Rejected by the checker; the action will NOT be executed. <c>RejectionReason</c> is populated.</summary>
    Rejected = 2,

    /// <summary>The TTL elapsed before any checker decided; the action will NOT be executed.</summary>
    Expired = 3,
}

/// <summary>
/// R2273 / TOR SEC 027 — lifecycle state of a <see cref="SensitiveAdminAction"/> request
/// in the generic 4-eyes admin workflow. Transitions:
/// <c>PendingApproval → Approved | Rejected | Cancelled | Expired</c>; from
/// <c>Approved</c> the row advances into <c>Executed</c> on handler success or
/// <c>ExecutionFailed</c> on handler error / missing handler. All non-PendingApproval
/// statuses are terminal — the row never re-enters PendingApproval.
/// </summary>
/// <remarks>
/// Distinct from <see cref="PendingAdminActionStatus"/> (R0058) which models the older
/// per-operation maker-checker queue. This enum is the generic 4-eyes substrate that
/// concrete sensitive-action policies (USER.ROLE_GRANT, EXECUTORY_DOC.CANCEL, …) hook
/// into via <c>ISensitiveActionPolicy</c> and <c>ISensitiveActionHandler</c>.
/// </remarks>
public enum SensitiveAdminActionStatus
{
    /// <summary>Request opened by the requester; awaiting a distinct approver.</summary>
    PendingApproval = 0,

    /// <summary>
    /// Approved by a second operator. The registered handler has been invoked but its
    /// outcome is captured in the follow-on <see cref="Executed"/> /
    /// <see cref="ExecutionFailed"/> transition.
    /// </summary>
    Approved = 1,

    /// <summary>Rejected by a second operator. The handler is NOT invoked.</summary>
    Rejected = 2,

    /// <summary>Cancelled (typically by the original requester before any approver decided).</summary>
    Cancelled = 3,

    /// <summary>The TTL elapsed before any approver decided. The handler is NOT invoked.</summary>
    Expired = 4,

    /// <summary>Approved AND the registered handler executed successfully.</summary>
    Executed = 5,

    /// <summary>
    /// Approved BUT the registered handler returned a failure (or no handler was
    /// registered for the action code). <c>ExecutionFailureReason</c> carries a
    /// sanitised string explaining the cause.
    /// </summary>
    ExecutionFailed = 6,
}

/// <summary>
/// R0166 / TOR CF 03.11 / UI 015 — lifecycle status of a <see cref="BulkOperationRun"/>.
/// Transitions are linear: <c>Pending → Running → (Completed | PartiallyFailed | Failed |
/// Cancelled)</c>. The terminal values are mutually exclusive — once stamped, the run
/// row is immutable.
/// </summary>
public enum BulkOperationStatus
{
    /// <summary>Row created but the runner has not yet entered the loop. Transient — the runner
    /// immediately flips to <see cref="Running"/> once the selection is resolved.</summary>
    Pending = 0,

    /// <summary>The runner is iterating the resolved row set; per-row outcomes are being
    /// counted into the run row.</summary>
    Running = 1,

    /// <summary>Every row succeeded — no entries in <c>FailureSummaryJson</c>.</summary>
    Completed = 2,

    /// <summary>At least one row succeeded and at least one row failed.
    /// <c>FailureSummaryJson</c> carries the failed-row details (capped at 100 entries).</summary>
    PartiallyFailed = 3,

    /// <summary>Every row failed. Indicates a systemic problem (operation parameters
    /// invalid, downstream service unavailable, FK constraint shifted) rather than the
    /// per-row anomaly pattern of <see cref="PartiallyFailed"/>.</summary>
    Failed = 4,

    /// <summary>The runner was cancelled (e.g. host shutdown) before the loop finished.
    /// Counters reflect the partial work that did complete; the selection remains
    /// consumed so the operator must create a fresh selection to resume.</summary>
    Cancelled = 5,
}

/// <summary>
/// R0127 / CF 16.11 — lifecycle state of a <see cref="UserAbsence"/> row.
/// Transitions: <c>Planned → Active → Completed</c>; <c>Planned → Cancelled</c> is the
/// only other path. <c>Active</c> rows MUST be Completed (not Cancelled) so the revert
/// sweep runs.
/// </summary>
public enum UserAbsenceStatus
{
    /// <summary>Planned — the absence window has been declared but not yet started.</summary>
    Planned = 0,

    /// <summary>Active — the lifecycle job has reached <c>StartDateUtc</c> and the
    /// absent user's open tasks have been routed to the delegate.</summary>
    Active = 1,

    /// <summary>Completed — the lifecycle job has reached <c>EndDateUtc</c> and every
    /// still-open delegated task has been reverted to its original assignee.</summary>
    Completed = 2,

    /// <summary>Cancelled — the admin removed the planned absence before it activated.
    /// Only valid as a transition out of <see cref="Planned"/>.</summary>
    Cancelled = 3,
}

/// <summary>
/// R0321 / R0224 / UI 008 — origin of an <see cref="ApplicationVersion"/> snapshot. Documents
/// WHY a particular row exists so operators can distinguish auto-save churn from explicit
/// citizen actions when auditing draft history.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pruning policy.</b> Only <see cref="Autosave"/> rows are eligible for auto-pruning
/// once <c>ApplicationAutosaveOptions.MaxAutosavesPerApplication</c> is exceeded;
/// <see cref="ManualSave"/>, <see cref="Submit"/>, and <see cref="Revert"/> rows are retained
/// indefinitely so an investigator can always reconstruct the explicit user-driven
/// version chain. The <c>ApplicationVersionService.SaveAsync</c> implementation
/// performs the cap enforcement in the same transaction as the insert.
/// </para>
/// <para>
/// <b>Enum-value stability.</b> The numeric values are part of the persistence contract
/// (they round-trip through the <c>Source</c> column as an <c>int</c>) — renumbering is
/// a breaking change that requires a data migration.
/// </para>
/// </remarks>
public enum ApplicationVersionSource
{
    /// <summary>Background auto-save tick — high frequency, eligible for pruning.</summary>
    Autosave = 0,

    /// <summary>Explicit "save draft" click by the citizen — never pruned.</summary>
    ManualSave = 1,

    /// <summary>The version captured at submission time (Draft → Submitted) — never pruned.</summary>
    Submit = 2,

    /// <summary>User revert action — produced by <c>RevertAsync</c> when restoring a prior version.</summary>
    Revert = 3,
}

/// <summary>
/// R0311 / TOR Annex 2.3 — civil-status classification of an <see cref="InsuredPerson"/>
/// (Persoană asigurată). Values are persisted as <c>int</c> via the
/// <c>ContributorCivilStatus.Status</c> column; numeric stability is part of the
/// persistence contract — renumbering is a breaking change that requires a data migration.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming", "CA1720:Identifier contains type name",
    Justification = "Civil-status value 'Single' is the domain term used in Romanian and English forms; renaming would diverge from the Annex 2.3 vocabulary.")]
public enum CivilStatusType
{
    /// <summary>Necăsătorit/ă — never married.</summary>
    Single = 0,

    /// <summary>Căsătorit/ă — currently married per the civil-status register.</summary>
    Married = 1,

    /// <summary>Divorțat/ă — currently divorced.</summary>
    Divorced = 2,

    /// <summary>Văduv/ă — currently widowed.</summary>
    Widowed = 3,

    /// <summary>Separat/ă — legally separated.</summary>
    Separated = 4,
}

/// <summary>
/// Lifecycle state of a <see cref="UserProfile"/> account per TOR SEC 016 (R0059). Distinct
/// from the soft-delete <c>IsActive</c> marker on the row — a soft-deleted row may carry
/// any state, but only <see cref="Active"/> permits authentication.
/// </summary>
/// <remarks>
/// <para>
/// <b>Authentication gate.</b> The MPass sign-in pipeline (and any local login flow)
/// rejects authentication for every state except <see cref="Active"/>. The check lives
/// in <c>UserDirectoryService.UpsertOnSignInAsync</c> so an OIDC token round-trip cannot
/// silently re-activate a non-Active account.
/// </para>
/// <para>
/// <b>Audit obligation.</b> Every transition MUST produce an <c>AuditLog</c> row with a
/// stable event code <c>USER.STATE_CHANGE.&lt;FROM&gt;.&lt;TO&gt;</c> and severity
/// <see cref="AuditSeverity.Critical"/>. The actor is the admin who triggered it
/// (from <c>ICallerContext.UserSqid</c>) or the literal <c>"system"</c> when the
/// transition is auto-driven (e.g. brute-force lockout via
/// <c>IUserAccountStateService.LockForFailedLoginsAsync</c>).
/// </para>
/// </remarks>
public enum UserAccountState
{
    /// <summary>Normal account; authentication accepted.</summary>
    Active = 0,

    /// <summary>Administratively suspended (temporary, e.g. pending investigation); authentication rejected.</summary>
    Suspended = 1,

    /// <summary>Administratively disabled (permanent, e.g. employee left); authentication rejected.</summary>
    Disabled = 2,

    /// <summary>Auto- or admin-locked (e.g. brute-force lockout); authentication rejected.</summary>
    Locked = 3,
}

/// <summary>
/// R0123 / TOR CF 16.05 — kind of a node in a <see cref="WorkflowDefinition"/>'s persisted
/// graph. The graph model is the deterministic substrate the workflow executor runs
/// against until the real Camunda/Operaton engine adapter lands; each node kind has a
/// distinct execution semantic enforced by the executor and the graph validator.
/// </summary>
/// <remarks>
/// <para>
/// <b>Enum-value stability.</b> The numeric values are part of the persistence contract
/// (they round-trip through the <c>Kind</c> column as <c>int</c>) — renumbering is a
/// breaking change that requires a data migration. Add new node kinds at the end.
/// </para>
/// <para>
/// <b>Execution semantics.</b>
/// <list type="bullet">
///   <item><see cref="Start"/> — exactly one per graph; the validator's entry point.</item>
///   <item><see cref="Task"/> — produces a <see cref="WorkflowTask"/> row when reached.</item>
///   <item><see cref="AndSplit"/> — fan-out: every outgoing edge spawns a sibling task; the
///         executor stamps each child's <see cref="WorkflowTask.ParentSplitTaskId"/>.</item>
///   <item><see cref="AndJoin"/> — barrier: the executor only advances past it when every
///         sibling under the matching <see cref="AndSplit"/> has completed.</item>
///   <item><see cref="OrSplit"/> — exclusive choice: the executor evaluates the node's
///         <see cref="WorkflowGraphNode.ConditionExpression"/> via the rule engine and
///         follows the matching outgoing edge; on a no-decision the first edge wins
///         (fail-open).</item>
///   <item><see cref="OrJoin"/> — merge point for an <see cref="OrSplit"/>: only one
///         branch ever reaches here so the executor simply forwards.</item>
///   <item><see cref="End"/> — terminal: the executor marks the workflow run complete.</item>
/// </list>
/// </para>
/// </remarks>
public enum WorkflowNodeKind
{
    /// <summary>Sole entry point of the graph. Exactly one Start node per workflow.</summary>
    Start = 0,

    /// <summary>Human-actionable task that materialises as a <see cref="WorkflowTask"/> row.</summary>
    Task = 1,

    /// <summary>AND-split (parallel fan-out): every outgoing edge spawns a sibling task.</summary>
    AndSplit = 2,

    /// <summary>AND-join (parallel barrier): waits until every sibling task has completed.</summary>
    AndJoin = 3,

    /// <summary>OR-split (exclusive choice): evaluates the condition to pick exactly one outgoing edge.</summary>
    OrSplit = 4,

    /// <summary>OR-join (exclusive merge): forwards immediately because only one branch arrives.</summary>
    OrJoin = 5,

    /// <summary>Terminal node: reaching it marks the workflow run as complete.</summary>
    End = 6,
}

/// <summary>
/// R0517 / TOR CF 02.05 — classification of a citizen benefit payment recorded
/// against a <c>Cnas.Ps.Core.Domain.BenefitPayment</c> row. The value drives the
/// citizen-portal label, the bucketing on the per-call totals
/// (<c>TotalPaidLast12Months</c> / <c>TotalScheduledNext3Months</c>), and the
/// downstream upstream-ledger routing once the real MTreasury / IPS adapter
/// lands.
/// </summary>
/// <remarks>
/// <para>
/// <b>Enum-value stability.</b> The numeric values are part of the persistence
/// contract (they round-trip through the <c>BenefitType</c> column as <c>int</c>)
/// — renumbering is a breaking change that requires a data migration. Append
/// new benefit kinds at the end.
/// </para>
/// <para>
/// <b>Wire representation.</b> The DTO surface (<c>BenefitPaymentDto.BenefitType</c>)
/// renders the enum as its stable <c>ToString()</c> name so the client UI can
/// switch on a self-describing label without owning the numeric mapping.
/// </para>
/// </remarks>
public enum BenefitType
{
    /// <summary>Old-age pension (pensie pentru limită de vârstă) — TOR §4.2.</summary>
    OldAgePension = 0,

    /// <summary>Disability pension (pensie de dizabilitate) — TOR §4.3.</summary>
    DisabilityPension = 1,

    /// <summary>Survivor pension (pensie de urmaș) — TOR §4.4.</summary>
    SurvivorPension = 2,

    /// <summary>Child allowance (indemnizație pentru copil) — Annex 2.5 §3.</summary>
    ChildAllowance = 3,

    /// <summary>Maternity allowance (indemnizație de maternitate) — Annex 2.5 §4.</summary>
    MaternityAllowance = 4,

    /// <summary>Unemployment allowance (indemnizație de șomaj) — Annex 2.5 §5.</summary>
    UnemploymentAllowance = 5,

    /// <summary>Social aid (ajutor social) — means-tested top-up per Annex 2.5 §6.</summary>
    SocialAid = 6,

    /// <summary>Burial allowance (ajutor de deces) — one-off payout per Annex 2.5 §7.</summary>
    BurialAllowance = 7,
}

/// <summary>
/// R0517 / TOR CF 02.05 — lifecycle state of a <c>Cnas.Ps.Core.Domain.BenefitPayment</c>
/// row. Transitions are linear: <c>Scheduled → Issued → Paid</c>; a payment may
/// terminate as <c>Returned</c> (postal-order / IBAN rejected) or <c>Cancelled</c>
/// (revoked before issue). The totals projection on the status DTO sums by this
/// value — only <see cref="Paid"/> entries contribute to <c>TotalPaidLast12Months</c>
/// and only <see cref="Scheduled"/> entries to <c>TotalScheduledNext3Months</c>.
/// </summary>
/// <remarks>
/// Numeric values are part of the persistence contract — renumbering is a
/// breaking change that requires a data migration.
/// </remarks>
public enum BenefitPaymentStatus
{
    /// <summary>Calendared but not yet issued to the disbursement channel.</summary>
    Scheduled = 0,

    /// <summary>Handed off to the disbursement channel (bank file / postal order printed).</summary>
    Issued = 1,

    /// <summary>Channel confirmed the funds reached the beneficiary.</summary>
    Paid = 2,

    /// <summary>The bank or postal channel returned the funds (closed IBAN, undelivered postal order).</summary>
    Returned = 3,

    /// <summary>The payment was cancelled before issue (entitlement revoked, beneficiary changed).</summary>
    Cancelled = 4,
}

/// <summary>
/// R0517 / TOR CF 02.05 — disbursement channel used for a <c>BenefitPayment</c>.
/// Drives the channel-specific fields on the entity (IBAN for
/// <see cref="BankTransfer"/>, postal-order number for <see cref="PostalOrder"/>,
/// none for <see cref="Cash"/>) and the eventual upstream-ledger routing.
/// </summary>
/// <remarks>
/// Numeric values are part of the persistence contract — renumbering is a
/// breaking change that requires a data migration.
/// </remarks>
public enum BenefitPaymentMethod
{
    /// <summary>SEPA / national bank transfer to the beneficiary IBAN.</summary>
    BankTransfer = 0,

    /// <summary>Moldovan post-office order, identified by <c>PostalOrderNumber</c>.</summary>
    PostalOrder = 1,

    /// <summary>Cash collection at a CNAS branch (legacy / fallback).</summary>
    Cash = 2,
}

/// <summary>
/// R0227 / TOR UI 014 — semantic category of an <c>AttachmentRecord</c>. Drives the
/// citizen-portal grouping ("Identity documents", "Income proofs", ...), the per-category
/// retention policy and the per-category review checklist surfaced to examiners. The
/// label is independent of the underlying MIME type — a PDF can be either an
/// <see cref="Income"/> proof or a <see cref="Medical"/> certificate depending on intent.
/// </summary>
/// <remarks>
/// <para>
/// <b>Enum-value stability.</b> The numeric values are part of the persistence contract
/// (they round-trip through the <c>Category</c> column as <c>int</c>) — renumbering is
/// a breaking change that requires a data migration. Append new categories at the end.
/// </para>
/// <para>
/// <b>Wire representation.</b> The DTO surface (<c>AttachmentRecordDto.Category</c>)
/// renders the enum as its stable <c>ToString()</c> name so the client UI can switch on
/// a self-describing label without owning the numeric mapping.
/// </para>
/// </remarks>
public enum AttachmentCategory
{
    /// <summary>Identity document (national id, passport, birth certificate).</summary>
    Identity = 0,

    /// <summary>Income proof (salary statement, tax declaration, bank statement).</summary>
    Income = 1,

    /// <summary>Medical certificate or report (disability evaluation, medical commission decision).</summary>
    Medical = 2,

    /// <summary>Legal document (court order, marriage certificate, divorce decree, power of attorney).</summary>
    LegalDocument = 3,

    /// <summary>Photo (passport-style photo, scanned snapshot).</summary>
    Photo = 4,

    /// <summary>Any attachment that does not fit a more specific category.</summary>
    Other = 99,
}

/// <summary>
/// R0524 / TOR CF 03.06 — visibility scope of a <see cref="SavedSearch"/>. The default
/// <see cref="Private"/> matches the R0165 behaviour (visible only to the owner). The
/// owner can flip the row to <see cref="Shared"/> (visible to every authenticated user
/// holding the <c>Search.View</c> permission) or to <see cref="Group"/> (visible to
/// every user whose <c>UserProfile.Groups</c> list contains the row's
/// <see cref="SavedSearch.SharedWithGroupCode"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Enum-value stability.</b> The numeric values are part of the persistence contract
/// (they round-trip through the <c>SharingScope</c> column as <c>int</c>) — renumbering
/// is a breaking change that requires a data migration. Append new scopes at the end.
/// </para>
/// <para>
/// <b>Wire representation.</b> The DTO surface
/// (<c>SavedSearchItem.SharingScope</c> / <c>SavedSearchShareInput.SharingScope</c>)
/// renders the enum as its stable <c>ToString()</c> name so the client UI can switch
/// on a self-describing label without owning the numeric mapping.
/// </para>
/// <para>
/// <b>Group-scope companion field.</b> When the scope is <see cref="Group"/> the row
/// MUST also populate <see cref="SavedSearch.SharedWithGroupCode"/>; for
/// <see cref="Private"/> and <see cref="Shared"/> that column MUST be <c>null</c>. The
/// service-layer share path enforces both halves of the invariant before persisting.
/// </para>
/// </remarks>
public enum SavedSearchSharingScope
{
    /// <summary>Default — visible only to the owner. Matches the R0165 behaviour.</summary>
    Private = 0,

    /// <summary>Visible to every authenticated user with <c>Search.View</c> permission.</summary>
    Shared = 1,

    /// <summary>Visible to every user whose <c>UserProfile.Groups</c> contains the
    /// <see cref="SavedSearch.SharedWithGroupCode"/> code on the row.</summary>
    Group = 2,
}

/// <summary>
/// R0810-R0813 / TOR BP 1.2 (Annex 8 — Declarații) — origin of a
/// <see cref="Declaration"/> row registered in the contributions registry. Each
/// value pins the upstream pipeline / document family that produced the row so
/// the monthly aggregator (R0813) can apply the correct duplicate-detection
/// rules and the audit trail can attribute the contribution to the right
/// administrative source.
/// </summary>
/// <remarks>
/// <para>
/// <b>Enum-value stability.</b> Numeric values are part of the persistence
/// contract (they round-trip through the <c>Kind</c> column as <c>int</c>) —
/// renumbering is a breaking change that requires a data migration. Append
/// new declaration kinds at the end.
/// </para>
/// <para>
/// <b>Wire representation.</b> The DTO surface
/// (<c>Cnas.Ps.Contracts.DeclarationDto.Kind</c>) renders the enum as its
/// stable <c>ToString()</c> name so the client UI can switch on a
/// self-describing label without owning the numeric mapping.
/// </para>
/// <para>
/// <b>Kind grouping by registration path.</b> Three registration endpoints
/// accept disjoint subsets of this enum:
/// <list type="bullet">
///   <item>R0810 / BP 1.2-A (SFS feed): <see cref="Sfs"/> only.</item>
///   <item>R0811 / BP 1.2-B (CNAS desk): <see cref="BassFour"/>,
///     <see cref="Bass"/>, <see cref="BassAn"/>, <see cref="Pre2018"/>.</item>
///   <item>R0812 / BP 1.2-C (other documents): <see cref="Control"/>,
///     <see cref="CourtDecision"/>, <see cref="Other"/>.</item>
/// </list>
/// The validators enforce the grouping; the service layer re-checks defensively.
/// </para>
/// </remarks>
public enum DeclarationKind
{
    /// <summary>R0810 — automated monthly feed from SI SFS (Serviciul Fiscal de Stat).</summary>
    Sfs = 0,

    /// <summary>R0811 — paper 4-BASS declaration submitted at a CNAS desk (current form).</summary>
    BassFour = 1,

    /// <summary>R0811 — paper BASS declaration submitted at a CNAS desk (variant form).</summary>
    Bass = 2,

    /// <summary>R0811 — paper BASS-AN (annual) declaration submitted at a CNAS desk.</summary>
    BassAn = 3,

    /// <summary>R0811 — legacy pre-2018 declaration registered at a CNAS desk.</summary>
    Pre2018 = 4,

    /// <summary>R0812 — contribution recalculated from a control / inspection report.</summary>
    Control = 5,

    /// <summary>R0812 — contribution recalculated from a court decision.</summary>
    CourtDecision = 6,

    /// <summary>R0812 — contribution recorded from any other supporting document.</summary>
    Other = 7,
}

/// <summary>
/// R0810-R0813 / TOR BP 1.2 (Annex 8 — Declarații) — lifecycle state of a
/// <see cref="Declaration"/> row. Transitions are linear: <c>Received →
/// Validated → Adjusted</c>; any state may transition to <c>Cancelled</c>
/// before reconciliation. The monthly aggregator (R0813) excludes
/// <see cref="Cancelled"/> rows from totals but counts every other status.
/// </summary>
/// <remarks>
/// Numeric values are part of the persistence contract — renumbering is a
/// breaking change that requires a data migration.
/// </remarks>
public enum DeclarationStatus
{
    /// <summary>Default — row inserted but not yet reconciled with control / TGS data.</summary>
    Received = 0,

    /// <summary>Reviewed and accepted as-is by an operator or automated control.</summary>
    Validated = 1,

    /// <summary>An <c>AdjustedContributionAmount</c> supersedes the declared figure.</summary>
    Adjusted = 2,

    /// <summary>Cancelled — excluded from <see cref="MonthlyContributionCalculation"/> totals.</summary>
    Cancelled = 3,
}

/// <summary>
/// R0910 / TOR BP 2.2-A — lifecycle state of a <see cref="Rev5Declaration"/>
/// header row. Transitions are linear: <c>Received → Validated → Adjusted</c>;
/// any state may transition to <c>Cancelled</c>, which rolls back every
/// <see cref="PersonalAccountEntry"/> the declaration projected.
/// </summary>
/// <remarks>
/// Numeric values are part of the persistence contract — renumbering is a
/// breaking change that requires a data migration. Append new states at the
/// end.
/// </remarks>
public enum Rev5DeclarationStatus
{
    /// <summary>Default — header inserted but not yet reconciled.</summary>
    Received = 0,

    /// <summary>Reviewed and accepted as-is by an operator.</summary>
    Validated = 1,

    /// <summary>One or more child rows have been adjusted post-registration.</summary>
    Adjusted = 2,

    /// <summary>Cancelled — projected <see cref="PersonalAccountEntry"/> rows are rolled back.</summary>
    Cancelled = 3,
}

/// <summary>
/// R0911 / TOR BP 2.2-B — lifecycle state of a <see cref="TreasuryPaymentReceipt"/>
/// as the daily Treasury feed reconciliation distributes the payment to the
/// insured persons named in the matching <see cref="Rev5DeclarationRow"/> rows.
/// </summary>
/// <remarks>
/// <para>
/// The receipt lands in <see cref="Pending"/> after import; the
/// <c>TreasuryDistributionJob</c> background job flips it to a terminal state
/// when reconciliation runs.
/// </para>
/// <para>
/// <see cref="PartiallyDistributed"/> means a non-zero
/// <see cref="TreasuryPaymentReceipt.UndistributedRemainderAmount"/> remained
/// after the proportional split — typically because not every Solicitant in
/// the matching REV-5 rows has a personal account on file.
/// </para>
/// </remarks>
public enum TreasuryPaymentDistributionStatus
{
    /// <summary>Default — receipt imported but not yet distributed.</summary>
    Pending = 0,

    /// <summary>Fully distributed across the matching REV-5 rows.</summary>
    Distributed = 1,

    /// <summary>
    /// Partially distributed — some matching rows resolved into personal-account
    /// entries but a non-zero remainder could not be attributed.
    /// </summary>
    PartiallyDistributed = 2,

    /// <summary>
    /// Distribution failed — no matching REV-5 rows exist for the receipt's
    /// (PayerContributor × ReportingMonth) tuple. The entire
    /// <see cref="TreasuryPaymentReceipt.AmountReceived"/> sits in
    /// <see cref="TreasuryPaymentReceipt.UndistributedRemainderAmount"/>.
    /// </summary>
    Failed = 3,
}

/// <summary>
/// R0831 / TOR BP 1.3-B — classification of a <see cref="Claim"/> (creanță).
/// Drives the reporting bucketing on the claims registry and the
/// <c>cnas.claim.registered{kind}</c> counter cardinality.
/// </summary>
/// <remarks>
/// <para>
/// <b>Enum-value stability.</b> Numeric values are part of the persistence
/// contract (they round-trip through the <c>Kind</c> column as <c>int</c>) —
/// renumbering is a breaking change that requires a data migration. Append
/// new claim kinds at the end.
/// </para>
/// <para>
/// <b>Wire representation.</b> The DTO surface
/// (<c>Cnas.Ps.Contracts.ClaimDto.Kind</c>) renders the enum as its stable
/// <c>ToString()</c> name so the client UI can switch on a self-describing
/// label without owning the numeric mapping.
/// </para>
/// </remarks>
public enum ClaimKind
{
    /// <summary>Unpaid contribution principal owed by the payer.</summary>
    Contribution = 0,

    /// <summary>Late-payment penalty (majorare de întârziere) — pairs with R0819.</summary>
    LatePenalty = 1,

    /// <summary>Administrative fine assessed by a control body.</summary>
    AdminFine = 2,

    /// <summary>Court-ordered assessment.</summary>
    Court = 3,

    /// <summary>Any claim that does not fit a more specific kind.</summary>
    Other = 4,
}

/// <summary>
/// R0831 / R0832 / TOR BP 1.3-B / BP 1.3-C — lifecycle state of a
/// <see cref="Claim"/>. Transitions:
/// <list type="bullet">
///   <item><c>Open → PartiallyPaid</c> on first partial payment.</item>
///   <item><c>Open | PartiallyPaid → Settled</c> when the running total reaches the principal.</item>
///   <item><c>Open | PartiallyPaid → Cancelled</c> via admin action.</item>
///   <item><c>Open | PartiallyPaid → Disputed</c> via admin action; from Disputed only ModifyAsync or CancelAsync are accepted.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// <b>Enum-value stability.</b> Numeric values are part of the persistence
/// contract — renumbering is a breaking change. Append new states at the end.
/// </para>
/// </remarks>
public enum ClaimStatus
{
    /// <summary>Default — the claim is outstanding and no payment has been registered.</summary>
    Open = 0,

    /// <summary>At least one payment received, running total is still less than the principal.</summary>
    PartiallyPaid = 1,

    /// <summary>Fully paid — running total equals the principal. <c>SettledDate</c> is populated.</summary>
    Settled = 2,

    /// <summary>Administratively cancelled by an admin. <c>CancelReason</c> + <c>CancelledDate</c> are populated.</summary>
    Cancelled = 3,

    /// <summary>The contributor is contesting the obligation. Only Modify / Cancel transitions are accepted while in this state.</summary>
    Disputed = 4,
}

/// <summary>
/// R0814 / TOR BP 1.2-E — lifecycle state of a
/// <see cref="BassRefund"/> row. Transitions are linear:
/// <c>Requested → Approved → IssuedToTreasury → Confirmed</c>; the refund
/// may also flip to <c>Cancelled</c> from either <c>Requested</c> or
/// <c>Approved</c> (but NOT from <c>IssuedToTreasury</c> / <c>Confirmed</c>
/// — at that point the Treasury has already wired the money).
/// </summary>
/// <remarks>
/// <para>
/// <b>Enum-value stability.</b> Numeric values are part of the persistence
/// contract — renumbering is a breaking change. Append new states at the end.
/// </para>
/// <para>
/// <b>Wire representation.</b> The DTO surface
/// (<c>Cnas.Ps.Contracts.BassRefundDto.Status</c>) renders the enum as its
/// stable <c>ToString()</c> name so the client UI can switch on a
/// self-describing label without owning the numeric mapping.
/// </para>
/// </remarks>
public enum BassRefundStatus
{
    /// <summary>Default — refund instruction has been requested but not yet approved.</summary>
    Requested = 0,

    /// <summary>An admin signed off on the refund; ready to be dispatched to Treasury.</summary>
    Approved = 1,

    /// <summary>The dispatch instruction has been sent to the State Treasury.</summary>
    IssuedToTreasury = 2,

    /// <summary>The Treasury confirmed the refund landed in the payer's account.</summary>
    Confirmed = 3,

    /// <summary>Administratively cancelled — only allowed from <c>Requested</c> or <c>Approved</c>.</summary>
    Cancelled = 4,
}

/// <summary>
/// R0815 / TOR BP 1.2-F — kind of a
/// <see cref="PaymentCorrection"/> applied to an underlying
/// <see cref="TreasuryPaymentReceipt"/>. The four kinds cover the
/// "mis-routed" and "wrong-amount" remediation paths an operator may need.
/// </summary>
/// <remarks>
/// <para>
/// <b>Enum-value stability.</b> Numeric values are part of the persistence
/// contract — renumbering is a breaking change. Append new kinds at the end.
/// </para>
/// </remarks>
public enum PaymentCorrectionKind
{
    /// <summary>
    /// Reverse the entire receipt — flips <see cref="TreasuryPaymentReceipt.DistributionStatus"/>
    /// to <see cref="TreasuryPaymentDistributionStatus.Failed"/> and parks the full
    /// <c>AmountReceived</c> in <c>UndistributedRemainderAmount</c> so the receipt can be
    /// redistributed or refunded.
    /// </summary>
    Reverse = 0,

    /// <summary>Re-route the receipt to a different paying contributor (wrong-payer mis-route).</summary>
    RedirectToPayer = 1,

    /// <summary>Re-route the receipt to a different reporting month (wrong-month mis-route).</summary>
    RedirectToMonth = 2,

    /// <summary>Override the receipt's <c>AmountReceived</c> to a corrected value (over-paid scenario).</summary>
    AdjustAmount = 3,
}

/// <summary>
/// R0815 / TOR BP 1.2-F — lifecycle state of a
/// <see cref="PaymentCorrection"/>. Transitions are linear:
/// <c>Draft → Approved → Applied</c>; the row may also transition to
/// <c>Cancelled</c> from <c>Draft</c> (only). Once <c>Applied</c> the row is
/// terminal and the receipt mutation has been performed.
/// </summary>
/// <remarks>
/// Numeric values are part of the persistence contract — renumbering is a
/// breaking change.
/// </remarks>
public enum PaymentCorrectionStatus
{
    /// <summary>Default — correction recorded but not yet signed off by an admin.</summary>
    Draft = 0,

    /// <summary>Admin signed off; the correction is ready to be applied.</summary>
    Approved = 1,

    /// <summary>Correction has been applied — the receipt mutation is persisted.</summary>
    Applied = 2,

    /// <summary>Administratively cancelled from <c>Draft</c>. Terminal.</summary>
    Cancelled = 3,
}

/// <summary>
/// R0817 / TOR BP 1.2-H — lifecycle state of a
/// <c>Cnas.Ps.Core.Domain.PenaltyRepaymentPlan</c>. Transitions:
/// <list type="bullet">
///   <item><c>Active → Completed</c> once the last installment is paid.</item>
///   <item><c>Active → Defaulted</c> when the background detector observes any installment
///     past its due date for more than 30 days without payment.</item>
///   <item><c>Active → Cancelled</c> via an explicit admin cancellation.</item>
/// </list>
/// <c>Completed</c>, <c>Defaulted</c>, and <c>Cancelled</c> are terminal.
/// </summary>
/// <remarks>
/// <para>
/// <b>Enum-value stability.</b> Numeric values are part of the persistence
/// contract — renumbering is a breaking change. Append new states at the end.
/// </para>
/// </remarks>
public enum PenaltyRepaymentPlanStatus
{
    /// <summary>Default — the plan is in flight; installments are still being paid.</summary>
    Active = 0,

    /// <summary>Every installment has been paid in full. Terminal.</summary>
    Completed = 1,

    /// <summary>
    /// At least one installment is overdue by more than 30 days without payment.
    /// Operators triage Defaulted plans into the claims registry (R0831). Terminal.
    /// </summary>
    Defaulted = 2,

    /// <summary>Administratively cancelled by an admin. Terminal.</summary>
    Cancelled = 3,
}

/// <summary>
/// R0920 / TOR BP 2.3-A — lifecycle state of a <see cref="LaborBooklet"/>
/// master record. Transitions are linear:
/// <c>Pending → Verified</c> when an operator confirms the scanned booklet
/// matches the citizen's paper record, or
/// <c>Pending → Rejected</c> when the scanned image fails the operator's
/// verification (illegible scan, mismatched IDNP, ...). Both Verified and
/// Rejected are terminal — the registry expects a follow-up re-registration
/// rather than an in-place flip.
/// </summary>
/// <remarks>
/// Numeric values are part of the persistence contract — renumbering is a
/// breaking change that requires a data migration.
/// </remarks>
public enum LaborBookletStatus
{
    /// <summary>Default — booklet registered but not yet operator-verified.</summary>
    Pending = 0,

    /// <summary>Operator confirmed the booklet matches the citizen's paper original.</summary>
    Verified = 1,

    /// <summary>Operator rejected the booklet (illegible scan, mismatched citizen, ...).</summary>
    Rejected = 2,
}

/// <summary>
/// R1702 / TOR CF 14.12 / Annex 4 — lifecycle state of a benefit decision
/// (<c>BenefitDecision</c>) carried over the Annex-4 surface. The wire
/// vocabulary is intentionally narrower than the internal decision
/// state-machine — the Annex-4 op only ever surfaces <see cref="Active"/>
/// and <see cref="Suspended"/>; cancelled / superseded / expired rows are
/// filtered out before they reach the response shape.
/// </summary>
/// <remarks>
/// Numeric values are part of the persistence contract — renumbering is a
/// breaking change that requires a data migration.
/// </remarks>
public enum BenefitDecisionStatus
{
    /// <summary>Decision is in force and paying out per the effective window.</summary>
    Active = 0,

    /// <summary>Decision is on file but temporarily suspended (administrative review, beneficiary unreachable).</summary>
    Suspended = 1,

    /// <summary>Decision has been cancelled by the issuing branch (revoked entitlement).</summary>
    Cancelled = 2,
}

/// <summary>
/// R1703 / TOR CF 14.12 / Annex 4 — per-period payment status of a benefit
/// decision over the Annex-4 surface. Mirrors the citizen-portal "this
/// month is …" widget the consuming systems are replicating. The values
/// are narrower than the full <see cref="BenefitPaymentStatus"/> ledger —
/// the Annex-4 surface intentionally collapses <c>Scheduled</c> /
/// <c>Issued</c> into a single <see cref="Pending"/> bucket so the
/// consumer does not have to model the internal disbursement pipeline.
/// </summary>
/// <remarks>
/// Numeric values are part of the persistence contract — renumbering is a
/// breaking change that requires a data migration.
/// </remarks>
public enum AnnexFourPaymentStatus
{
    /// <summary>Payment has been scheduled / issued but the channel has not yet confirmed delivery.</summary>
    Pending = 0,

    /// <summary>Channel confirmed the funds reached the beneficiary.</summary>
    Paid = 1,

    /// <summary>Channel returned the funds (closed IBAN, undelivered postal order).</summary>
    Returned = 2,

    /// <summary>Payment was suspended for this period (decision under review).</summary>
    Suspended = 3,
}

/// <summary>
/// R1703 / TOR CF 14.12 / Annex 4 — disbursement channel surfaced over the
/// Annex-4 payment-status response. Mirrors
/// <see cref="BenefitPaymentMethod"/> but uses the consumer-facing names
/// <c>CashPayout</c> instead of the internal <c>Cash</c> label so the
/// external surface does not leak operator slang. The mapping
/// <see cref="BenefitPaymentMethod.Cash"/> -&gt; <see cref="CashPayout"/>
/// is one-to-one.
/// </summary>
/// <remarks>
/// Numeric values are part of the persistence contract — renumbering is a
/// breaking change that requires a data migration.
/// </remarks>
public enum PaymentChannelKind
{
    /// <summary>SEPA / national bank transfer to the beneficiary IBAN.</summary>
    BankTransfer = 0,

    /// <summary>Moldovan post-office order, identified by the postal-order number.</summary>
    PostalOrder = 1,

    /// <summary>Cash collection at a CNAS branch (legacy / fallback).</summary>
    CashPayout = 2,
}

/// <summary>
/// R1704 / TOR CF 14.12 / Annex 4 — payer category surfaced over the
/// Annex-4 payer-data response. Drives the
/// <c>PayerDataDto.CountOfInsuredEmployees</c> branch — only
/// <see cref="LegalEntity"/> rows carry a non-zero employee count.
/// </summary>
/// <remarks>
/// Numeric values are part of the persistence contract — renumbering is a
/// breaking change that requires a data migration.
/// </remarks>
public enum PayerKind
{
    /// <summary>Self-employed contributor (resolved via IDNP).</summary>
    NaturalPerson = 0,

    /// <summary>Company / NGO / institution (resolved via IDNO).</summary>
    LegalEntity = 1,
}

/// <summary>
/// R1704 / TOR CF 14.12 / Annex 4 — payer lifecycle state surfaced over the
/// Annex-4 payer-data response. Narrower than the internal payer-registry
/// state-machine; <see cref="Active"/> covers every non-suspended /
/// non-closed lifecycle slice in the source registry.
/// </summary>
/// <remarks>
/// Numeric values are part of the persistence contract — renumbering is a
/// breaking change that requires a data migration.
/// </remarks>
public enum PayerLifecycleStatus
{
    /// <summary>Payer is currently registered and accepting declarations.</summary>
    Active = 0,

    /// <summary>Payer is on file but temporarily suspended (administrative review).</summary>
    Suspended = 1,

    /// <summary>Payer has been closed / liquidated and no longer files declarations.</summary>
    Closed = 2,
}

/// <summary>
/// R1706 / TOR CF 14.12 / Annex 4 — per-month declaration filing state
/// surfaced over the Annex-4 contribution-payment-info response. Narrower
/// than the internal declaration-pipeline state-machine — only the three
/// states a consumer cares about reach the response shape.
/// </summary>
/// <remarks>
/// Numeric values are part of the persistence contract — renumbering is a
/// breaking change that requires a data migration.
/// </remarks>
public enum DeclarationFilingStatus
{
    /// <summary>Declaration filed on time for the reporting month.</summary>
    Filed = 0,

    /// <summary>No declaration on file for the reporting month.</summary>
    NotFiled = 1,

    /// <summary>Declaration filed after the statutory deadline.</summary>
    Late = 2,
}

/// <summary>
/// R1600 / TOR Annex 3.8 / R1406 §3.6-G — classification of an executory document
/// (document executoriu) that compels withholding of a portion of a person's
/// income to satisfy a debt. Drives the audit-trail bucketing and the reporting
/// view that aggregates per-issuer volume.
/// </summary>
/// <remarks>
/// Stable enum-name strings are persisted at rest (mirrors the
/// <see cref="ApplicantKind"/> / <see cref="DeclarationKind"/> convention).
/// Renaming a member is a breaking change that requires a data migration.
/// </remarks>
public enum ExecutoryDocumentKind
{
    /// <summary>
    /// Court-issued order (titlu executoriu emis de instanță) — child support,
    /// civil judgments, family-court orders. The default classification.
    /// </summary>
    CourtOrder = 0,

    /// <summary>
    /// Bailiff-issued enforcement order (ordonanță executor judecătoresc) — issued
    /// by an executor judecătoresc enforcing a prior court decision.
    /// </summary>
    BailiffOrder = 1,

    /// <summary>
    /// Notary-issued executory writ (act notarial cu titlu executoriu) — issued
    /// by a notary for incontestable debts under art. 36 al. (1) Codul de
    /// procedură civilă.
    /// </summary>
    NotaryOrder = 2,

    /// <summary>
    /// Administrative enforcement order (decizie administrativă cu putere
    /// executorie) — e.g. fiscal-control assessments enforceable without prior
    /// court action.
    /// </summary>
    AdministrativeOrder = 3,

    /// <summary>
    /// Catch-all for any executory instrument that does not fit the dedicated
    /// kinds (foreign-court orders processed via Hague convention, ...).
    /// </summary>
    Other = 99,
}

/// <summary>
/// R1600 / R1406 — lifecycle state of an
/// <c>Cnas.Ps.Core.Domain.ExecutoryDocument</c>. Drives the withholding
/// calculator which only considers <see cref="Active"/> rows for the
/// beneficiary's benefit period.
/// </summary>
/// <remarks>
/// Stable enum-name strings are persisted at rest. Numeric values are part of
/// the persistence contract — renumbering is a breaking change.
/// </remarks>
public enum ExecutoryDocumentStatus
{
    /// <summary>Active — withholding is honoured on every benefit payment that falls within the effective window.</summary>
    Active = 0,

    /// <summary>
    /// Suspended — withholding is paused (court hearing, debtor appeal). The
    /// row remains in the registry; the calculator skips it until it is
    /// resumed.
    /// </summary>
    Suspended = 1,

    /// <summary>
    /// Completed — the debt was fully repaid (TotalWithheldMdl reached
    /// TotalOwedMdl) or the operator explicitly closed the document. Terminal
    /// state; no further withholdings are recorded.
    /// </summary>
    Completed = 2,

    /// <summary>
    /// Cancelled — the document was revoked (court reversal, fraudulent
    /// document, ...). Terminal state; carries a CancellationReason rationale.
    /// </summary>
    Cancelled = 3,
}

/// <summary>
/// R1600 / R1406 — withholding-calculation mode for an executory document.
/// Determines how the calculator translates the document's stored parameters
/// into a per-payment withholding amount.
/// </summary>
/// <remarks>
/// Stable enum-name strings are persisted at rest. Each mode reads a different
/// subset of the document's amount columns:
/// <list type="bullet">
///   <item><see cref="FixedAmount"/> → consumes <c>WithholdingAmountMdl</c>.</item>
///   <item><see cref="Percentage"/> → consumes <c>WithholdingPercentage</c>.</item>
///   <item><see cref="FullExcessOverMinimum"/> → consumes neither; computed from the benefit and legal-minimum-subsistence figures supplied to the calculator.</item>
/// </list>
/// </remarks>
public enum ExecutoryDocumentWithholdingMode
{
    /// <summary>Withhold a fixed MDL amount per payment (e.g. monthly child support of 1000 MDL).</summary>
    FixedAmount = 0,

    /// <summary>Withhold a percentage of the gross benefit (0..70%; child support / damages-for-injury may go up to 70% per art. 156 CMP).</summary>
    Percentage = 1,

    /// <summary>
    /// Withhold every leu above the legal minimum-subsistence floor — used
    /// when the court order targets disposable income above the protected
    /// minimum (e.g. high-income debt enforcement).
    /// </summary>
    FullExcessOverMinimum = 2,
}

/// <summary>
/// R2270 / TOR SEC 023-024 — classification of a <c>UserGroup</c> aggregate
/// driving the UI grouping and the per-kind audit-trail bucketing. Stable
/// enum-name strings are persisted at rest so humans can read group rows
/// directly without owning the numeric mapping.
/// </summary>
/// <remarks>
/// Numeric values are part of the persistence contract — renumbering is a
/// breaking change. Append new kinds at the end.
/// </remarks>
public enum UserGroupKind
{
    /// <summary>Organisational unit (e.g. a CNAS branch or a department).</summary>
    OrganizationalUnit = 0,

    /// <summary>Functional team (cross-org grouping aligned to a workflow / mission).</summary>
    FunctionalTeam = 1,

    /// <summary>Project-scoped group (time-bounded delivery teams).</summary>
    Project = 2,

    /// <summary>Catch-all for groups that do not fit a more specific kind.</summary>
    Custom = 99,
}

/// <summary>
/// R2270 / TOR SEC 023-024 — lifecycle state of a <c>UserGroup</c>. Disabled
/// groups remain visible to administrators but contribute NO roles to the
/// transitive resolution performed by <c>IUserGroupRoleResolver</c>.
/// </summary>
/// <remarks>
/// Numeric values are part of the persistence contract — renumbering is a
/// breaking change.
/// </remarks>
public enum UserGroupStatus
{
    /// <summary>Default — group is active and its roles contribute to resolution.</summary>
    Active = 0,

    /// <summary>Group is disabled — kept for history but does NOT contribute roles.</summary>
    Disabled = 1,
}

/// <summary>
/// R1707 / TOR CF 14.12 / Annex 4 — applicable EU-equivalent posting form
/// surfaced over the Annex-4 legal-applicable-form response. Mirrors the
/// EU Regulation 883/2004 vocabulary — <see cref="A1Equivalent"/> is the
/// modern form (post-2010), <see cref="E101Equivalent"/> the legacy
/// pre-2010 form. Bilateral agreements may use either depending on the
/// vintage of the agreement.
/// </summary>
/// <remarks>
/// Numeric values are part of the persistence contract — renumbering is a
/// breaking change that requires a data migration.
/// </remarks>
public enum LegalAgreementApplicableForm
{
    /// <summary>No agreement is in force for the (citizen, agreement-code) tuple.</summary>
    NotApplicable = 0,

    /// <summary>Modern A1 posting form (post-2010 vintage agreements).</summary>
    A1Equivalent = 1,

    /// <summary>Legacy E101 posting form (pre-2010 vintage agreements).</summary>
    E101Equivalent = 2,
}

/// <summary>
/// R2282 / TOR SEC 036 — origin of an <c>IntegrityCheckRun</c>. Distinguishes
/// scheduled background sweeps from operator-triggered ad-hoc runs so the
/// audit / dashboard surface can tell the two apart.
/// </summary>
/// <remarks>
/// Numeric values are part of the persistence contract — renumbering is a
/// breaking change.
/// </remarks>
public enum IntegrityCheckTriggerKind
{
    /// <summary>The run was fired by the Quartz <c>IntegrityCheckJob</c> on its nightly cadence.</summary>
    Scheduled = 0,

    /// <summary>The run was fired on demand by an operator via the admin endpoint.</summary>
    Manual = 1,
}

/// <summary>
/// R2282 / TOR SEC 036 — lifecycle status of an <c>IntegrityCheckRun</c>.
/// Tracks whether the integrity sweep is still in flight, completed
/// successfully, or aborted on an unhandled exception.
/// </summary>
/// <remarks>
/// Numeric values are part of the persistence contract — renumbering is a
/// breaking change.
/// </remarks>
public enum IntegrityCheckRunStatus
{
    /// <summary>The run is currently executing; <c>RunCompletedAt</c> is null.</summary>
    Running = 0,

    /// <summary>The run completed successfully (with or without findings).</summary>
    Completed = 1,

    /// <summary>The run aborted on an unhandled exception; <c>FailureReason</c> is populated.</summary>
    Failed = 2,
}

/// <summary>
/// R2282 / TOR SEC 036 — severity of an integrity-check finding. Drives
/// the operator dashboard's sort order and the pager-on-call gate.
/// </summary>
/// <remarks>
/// Numeric values are part of the persistence contract — renumbering is a
/// breaking change.
/// </remarks>
public enum IntegrityFindingSeverity
{
    /// <summary>Invariant violation that breaks correctness or security (e.g. missing IDNP hash).</summary>
    Critical = 0,

    /// <summary>High-impact violation that suppresses correct behaviour (e.g. withholding overflow).</summary>
    High = 1,

    /// <summary>Medium-impact violation worth investigating (e.g. orphan FK).</summary>
    Medium = 2,

    /// <summary>Low-impact violation flagged for awareness only.</summary>
    Low = 3,
}

/// <summary>
/// R1906 / TOR Annex 6 — channel through which a finalised report run is
/// fanned out to its configured recipients. Drives the per-channel
/// sub-dispatcher selection inside
/// <c>Cnas.Ps.Application.Reporting.IReportDistributionDispatcher</c>.
/// </summary>
/// <remarks>
/// Persisted as the stable enum-name string at rest (consistent with the
/// rest of the lifecycle enums under <c>cnas</c>). Renaming a member is a
/// breaking change that requires a data migration.
/// </remarks>
public enum ReportDistributionChannel
{
    /// <summary>Web inbox inside SI PS (the operator's notification tab after login).</summary>
    InSystem = 0,

    /// <summary>Operational / KPI dashboards — a separate widget surface from <see cref="InSystem"/>.</summary>
    Dashboard = 1,

    /// <summary>Email — dispatched through whatever email transport is wired (none by default).</summary>
    Email = 2,

    /// <summary>MNotify — government multi-channel notification dispatch (UC22).</summary>
    MNotify = 3,
}

/// <summary>
/// R1906 / TOR Annex 6 — interpretation of a
/// <c>Cnas.Ps.Core.Domain.ReportDistributionRule.RecipientCode</c> string.
/// Determines whether the recipient expression resolves to one user / a
/// group / a role / a raw email address / an MNotify category.
/// </summary>
/// <remarks>
/// Persisted as the stable enum-name string at rest. The kind drives both
/// the validation rule applied to <c>RecipientCode</c> (e.g. email regex
/// for <see cref="EmailAddress"/>) and the resolver branch that fans the
/// rule out to one or more concrete recipients before the channel handler
/// is invoked.
/// </remarks>
public enum ReportRecipientKind
{
    /// <summary>Single user — <c>RecipientCode</c> is a user Sqid or login.</summary>
    User = 0,

    /// <summary>User group — <c>RecipientCode</c> is the group's stable code; the resolver fans out to direct members.</summary>
    Group = 1,

    /// <summary>Role — <c>RecipientCode</c> is the role code; the resolver fans out to every user effectively holding it.</summary>
    Role = 2,

    /// <summary>Raw email — <c>RecipientCode</c> is an RFC-5322 mailbox; stored encrypted-at-rest with a hash shadow.</summary>
    EmailAddress = 3,

    /// <summary>MNotify category — <c>RecipientCode</c> is an upstream MNotify category code; MNotify handles the fan-out.</summary>
    MNotifyCategory = 4,
}

/// <summary>
/// R1906 / TOR Annex 6 — payload-format the recipient expects when a report
/// is delivered. The dispatcher matches this against the incoming
/// <c>ReportDispatchInputDto.Format</c>; rules whose format does not match
/// are <c>Skipped</c> rather than failing.
/// </summary>
/// <remarks>
/// Persisted as the stable enum-name string at rest. <see cref="LinkOnly"/>
/// is a special "no payload" sentinel — the recipient gets a deep link
/// rather than the rendered report body, which is the privacy-preserving
/// default for sensitive distributions.
/// </remarks>
public enum ReportDeliveryFormat
{
    /// <summary>PDF — the rendered report body is delivered as an attachment.</summary>
    Pdf = 0,

    /// <summary>CSV — tabular export delivered as an attachment.</summary>
    Csv = 1,

    /// <summary>XLSX — Excel workbook delivered as an attachment.</summary>
    Xlsx = 2,

    /// <summary>Link only — the recipient receives a deep link to the in-system viewer; no payload is attached.</summary>
    LinkOnly = 3,
}

/// <summary>
/// R1906 / TOR Annex 6 — relative urgency tagged on a distribution rule.
/// Surfaces to channels that support per-message urgency (MNotify carries
/// the value verbatim; email maps to an X-Priority header; the in-system
/// inbox renders a visual badge). Cardinality is bounded.
/// </summary>
/// <remarks>
/// Persisted as the stable enum-name string at rest. <see cref="Normal"/>
/// is the default. <see cref="Critical"/> reports SHOULD pager on-call if
/// the channel supports it.
/// </remarks>
public enum ReportDeliveryPriority
{
    /// <summary>Routine — default. No special handling.</summary>
    Normal = 0,

    /// <summary>High — surfaces above routine traffic in the operator inbox.</summary>
    High = 1,

    /// <summary>Critical — page on-call if the channel supports urgency.</summary>
    Critical = 2,
}

/// <summary>
/// R1906 / TOR Annex 6 — lifecycle status of one
/// <c>Cnas.Ps.Core.Domain.ReportDistributionDispatch</c> row. Captures the
/// outcome of a single attempt to fan a report run out through one rule's
/// channel.
/// </summary>
/// <remarks>
/// Persisted as the stable enum-name string at rest. <see cref="Pending"/>
/// is reserved for future async-dispatch flows; the synchronous dispatcher
/// always finalises a row before returning.
/// </remarks>
public enum ReportDispatchStatus
{
    /// <summary>The dispatcher has accepted the row but not yet attempted delivery.</summary>
    Pending = 0,

    /// <summary>The channel handler returned success — the recipient should have received the report.</summary>
    Delivered = 1,

    /// <summary>The channel handler returned a failure (transport error, missing config, ...). See <c>FailureReason</c>.</summary>
    Failed = 2,

    /// <summary>The dispatcher refused to deliver — format mismatch, channel not configured, recipient opted out, etc.</summary>
    Skipped = 3,
}

/// <summary>
/// R1503 / TOR §3.7-D — coarse scope of a <c>LegalChangeEvent</c>. Tells the
/// mass-recalculation engine which benefit kinds are potentially affected by
/// the change. <see cref="All"/> means "every kind known to the engine" —
/// the orchestrator snapshots the full benefit-kind list onto the event at
/// insert time so the scope is reproducible even if the enum grows.
/// </summary>
/// <remarks>
/// Persisted as the stable enum-name string at rest (mirrors the
/// <see cref="ApplicantKind"/> / <see cref="DeclarationKind"/> convention).
/// Renaming a member is a breaking change that requires a data migration.
/// </remarks>
public enum LegalChangeScope
{
    /// <summary>Affects pension benefits (old-age, disability, survivor).</summary>
    Pension = 0,

    /// <summary>Affects unemployment-allowance benefits.</summary>
    UnemploymentBenefit = 1,

    /// <summary>Affects maternity-indemnity benefits.</summary>
    MaternityIndemnity = 2,

    /// <summary>Affects temporary-incapacity indemnity benefits.</summary>
    IncapacityIndemnity = 3,

    /// <summary>Affects social-aid means-tested benefits.</summary>
    SocialAid = 4,

    /// <summary>
    /// Affects every benefit kind. The orchestrator snapshots the explicit
    /// <c>BenefitTypesInScope</c> list onto the event at insert time so the
    /// resolved scope is reproducible.
    /// </summary>
    All = 99,
}

/// <summary>
/// R1503 / TOR §3.7-D — lifecycle state of a <c>LegalChangeEvent</c>. The
/// operator authors the event in <see cref="Draft"/>, flips it to
/// <see cref="Ready"/> when the change set is complete, the engine drives the
/// row through <see cref="Recalculating"/> → <see cref="ReviewPending"/>
/// → <see cref="Applied"/>; any non-Applied state may be flipped to
/// <see cref="Cancelled"/>.
/// </summary>
/// <remarks>
/// Numeric values are part of the persistence contract — renumbering is a
/// breaking change. Append new states at the end.
/// </remarks>
public enum LegalChangeEventStatus
{
    /// <summary>Default — the operator is still authoring the event.</summary>
    Draft = 0,

    /// <summary>The event is fully authored and awaiting the next mass-recalculation sweep.</summary>
    Ready = 1,

    /// <summary>A recalculation run is currently in flight against this event.</summary>
    Recalculating = 2,

    /// <summary>The dry-run completed; per-decision results are awaiting operator review.</summary>
    ReviewPending = 3,

    /// <summary>The reviewed results have been applied to the affected decisions. Terminal.</summary>
    Applied = 4,

    /// <summary>Administratively cancelled before reaching <see cref="Applied"/>. Terminal.</summary>
    Cancelled = 5,
}

/// <summary>
/// R1503 / TOR §3.7-D — origin of a <c>RecalculationRun</c>. Distinguishes the
/// scheduled <c>MassRecalculationApplyJob</c> fire from an operator-triggered
/// ad-hoc run so the audit / dashboard surface can tell the two apart.
/// </summary>
/// <remarks>
/// Numeric values are part of the persistence contract — renumbering is a
/// breaking change.
/// </remarks>
public enum RecalculationTriggerKind
{
    /// <summary>The run was fired by the Quartz <c>MassRecalculationApplyJob</c>.</summary>
    Scheduled = 0,

    /// <summary>The run was fired on demand by an operator via the admin endpoint.</summary>
    Manual = 1,
}

/// <summary>
/// R1503 / TOR §3.7-D — execution mode of a <c>RecalculationRun</c>. Determines
/// whether the engine merely projects per-decision results (<see cref="DryRun"/>)
/// or also writes the new amounts back via the registered strategies
/// (<see cref="Apply"/>).
/// </summary>
/// <remarks>
/// Numeric values are part of the persistence contract — renumbering is a
/// breaking change.
/// </remarks>
public enum RecalculationMode
{
    /// <summary>Project per-decision results only — no benefit-decision rows are mutated.</summary>
    DryRun = 0,

    /// <summary>Project per-decision results AND write the new amounts back via strategy.ApplyAsync.</summary>
    Apply = 1,
}

/// <summary>
/// R1503 / TOR §3.7-D — lifecycle status of a <c>RecalculationRun</c>. The
/// orchestrator creates the row as <see cref="Running"/>, flips it to
/// <see cref="Completed"/> on success or <see cref="Failed"/> on an unhandled
/// exception. The mass-recalc prefix in the enum name disambiguates this from
/// other "RunStatus"-shaped enums in the codebase.
/// </summary>
/// <remarks>
/// Numeric values are part of the persistence contract — renumbering is a
/// breaking change.
/// </remarks>
public enum RecalculationRunStatus
{
    /// <summary>The run is currently executing; <c>CompletedAt</c> is null.</summary>
    Running = 0,

    /// <summary>The run completed successfully (with or without skipped/failed per-decision rows).</summary>
    Completed = 1,

    /// <summary>The run aborted on an unhandled exception; <c>FailureReason</c> is populated.</summary>
    Failed = 2,
}

/// <summary>
/// R1503 / TOR §3.7-D — per-decision outcome status inside a
/// <c>RecalculationRun</c>. The orchestrator stamps <see cref="Computed"/> on
/// every successfully-projected row during DryRun mode; <see cref="Skipped"/>
/// when no strategy is registered for the benefit kind OR the strategy
/// declined to recompute; <see cref="Failed"/> when the strategy threw;
/// <see cref="Rejected"/> when an operator explicitly cherry-picks a row out
/// of the apply set; <see cref="Applied"/> when the apply phase persisted the
/// new amount.
/// </summary>
/// <remarks>
/// Numeric values are part of the persistence contract — renumbering is a
/// breaking change.
/// </remarks>
public enum RecalculationResultStatus
{
    /// <summary>The new amount was projected; the row is eligible for apply.</summary>
    Computed = 0,

    /// <summary>The new amount was written back via strategy.ApplyAsync. Terminal.</summary>
    Applied = 1,

    /// <summary>The decision was scanned but skipped (no strategy registered, or strategy declined).</summary>
    Skipped = 2,

    /// <summary>The strategy threw while recomputing or applying. Terminal for this run.</summary>
    Failed = 3,

    /// <summary>The operator excluded the row from the apply set. Terminal for this run.</summary>
    Rejected = 4,
}

/// <summary>
/// R1710 / TOR INT 002 / Annex 4 — stable enum-name code of the Annex-4 op
/// targeted by an <c>OfflineBatchSubmission</c>. Each value mirrors a
/// synchronous <c>Cnas.Ps.Application.Interop.IInteropApi</c> method by
/// name so the batch processor can dispatch by string lookup.
/// </summary>
/// <remarks>
/// Numeric values are part of the persistence contract — renumbering is a
/// breaking change. The stable wire form is the enum-name string (e.g.
/// <c>"GetInsuredPersonStatus"</c>), not the numeric value.
/// </remarks>
public enum AnnexFourBatchOp
{
    /// <summary>Mirrors <c>IInteropApi.GetInsuredPersonStatusAsync</c>.</summary>
    GetInsuredPersonStatus = 0,

    /// <summary>Mirrors <c>IInteropApi.GetContributionHistoryAsync</c>.</summary>
    GetContributionHistory = 1,

    /// <summary>Mirrors <c>IInteropApi.GetBenefitsListAsync</c>.</summary>
    GetBenefitsList = 2,

    /// <summary>Mirrors <c>IInteropApi.GetPersonalAccountSnapshotAsync</c>.</summary>
    GetPersonalAccountSnapshot = 3,

    /// <summary>Mirrors <c>IInteropApi.GetActiveDecisionsAsync</c>.</summary>
    GetActiveDecisions = 4,

    /// <summary>Mirrors <c>IInteropApi.GetPaymentStatusAsync</c>.</summary>
    GetPaymentStatus = 5,

    /// <summary>Mirrors <c>IInteropApi.GetPayerDataAsync</c>.</summary>
    GetPayerData = 6,

    /// <summary>Mirrors <c>IInteropApi.IsBenefitBeneficiaryAsync</c>.</summary>
    IsBenefitBeneficiary = 7,

    /// <summary>Mirrors <c>IInteropApi.GetContributionPaymentInfoAsync</c>.</summary>
    GetContributionPaymentInfo = 8,

    /// <summary>Mirrors <c>IInteropApi.GetLegalApplicableFormAsync</c>.</summary>
    GetLegalApplicableForm = 9,

    /// <summary>Mirrors <c>IInteropApi.GetWorkInsurancePeriodAsync</c>.</summary>
    GetWorkInsurancePeriod = 10,
}

/// <summary>
/// R1710 / TOR INT 002 — lifecycle status of an <c>OfflineBatchSubmission</c>.
/// </summary>
/// <remarks>
/// <para>
/// Numeric values are part of the persistence contract. Renumbering is a
/// breaking change. Transitions:
/// <c>Submitted → Queued → Running → (Completed | Failed)</c>; both
/// <c>Submitted</c> and <c>Queued</c> may transition to <c>Cancelled</c>.
/// </para>
/// </remarks>
public enum OfflineBatchStatus
{
    /// <summary>Row inserted, request file persisted, rows being seeded.</summary>
    Submitted = 0,

    /// <summary>Ready for the Quartz job to pick up. Terminal-eligible for cancellation.</summary>
    Queued = 1,

    /// <summary>Job is iterating the rows. Cannot be cancelled.</summary>
    Running = 2,

    /// <summary>Response file generated, hashed and signed.</summary>
    Completed = 3,

    /// <summary>Processor crashed before the response file was produced; <c>FailureReason</c> populated.</summary>
    Failed = 4,

    /// <summary>Operator cancelled the submission before it began Running.</summary>
    Cancelled = 5,
}

/// <summary>
/// R1710 / TOR INT 002 — per-row outcome inside an <c>OfflineBatchSubmission</c>.
/// </summary>
/// <remarks>
/// Numeric values are part of the persistence contract. Renumbering is a
/// breaking change.
/// </remarks>
public enum OfflineBatchRowStatus
{
    /// <summary>Row seeded by the parser, awaiting processing.</summary>
    Pending = 0,

    /// <summary>Underlying interop call returned a successful <c>Result</c>.</summary>
    Succeeded = 1,

    /// <summary>
    /// Underlying interop call returned a failure (validation / not-found /
    /// conflict) or the parser tagged the row with a ParseError placeholder.
    /// </summary>
    Failed = 2,
}

/// <summary>
/// R1810 / TOR BP 1.2-I — lifecycle status of a daily Treasury feed import
/// run. Each row in <c>TreasuryFeedImports</c> advances through these states
/// monotonically; once a terminal state is reached the row is immutable.
/// </summary>
/// <remarks>
/// Numeric values are part of the persistence contract. Renumbering is a
/// breaking change. The unique index that prevents accidental double-ingest
/// for the same <c>(FeedDate, SourceKind)</c> filters on
/// <see cref="Completed"/> so a previously failed import can be retried.
/// </remarks>
public enum TreasuryFeedImportStatus
{
    /// <summary>Row inserted; the importer has not yet started fetching the feed file.</summary>
    Pending = 0,

    /// <summary>Importer is fetching the feed bytes from the configured source.</summary>
    Downloading = 1,

    /// <summary>Importer is parsing the CSV payload into per-row records.</summary>
    Parsing = 2,

    /// <summary>Importer is iterating the parsed rows and writing TreasuryReceipt updates.</summary>
    Importing = 3,

    /// <summary>Every row processed (or skipped); counters are final.</summary>
    Completed = 4,

    /// <summary>A non-recoverable failure occurred before completion; <c>FailureReason</c> populated.</summary>
    Failed = 5,

    /// <summary>The Quartz scheduler noticed a completed import already exists for the date; the run was a no-op.</summary>
    Skipped = 6,
}

/// <summary>
/// R1810 / TOR BP 1.2-I — origin of the Treasury feed file consumed by the
/// importer. The default production source is SFTP; the in-memory variant
/// drives tests and the HTTPS variant is a placeholder for future iterations.
/// </summary>
/// <remarks>
/// Numeric values are part of the persistence contract. Renumbering is a
/// breaking change.
/// </remarks>
public enum TreasuryFeedSourceKind
{
    /// <summary>HTTPS download from a Treasury-published URL.</summary>
    Https = 0,

    /// <summary>SFTP fetch from the Treasury's authenticated drop point.</summary>
    Sftp = 1,

    /// <summary>Operator manually uploaded a feed file via an admin endpoint.</summary>
    Manual = 2,

    /// <summary>Test-only — fixture rows materialised by the in-memory implementation.</summary>
    InMemoryTest = 3,
}

/// <summary>
/// R1810 / TOR BP 1.2-I — origin of the run that triggered a Treasury feed
/// import. Scheduled runs are auto-driven by the Quartz job; manual runs
/// are explicitly invoked by an admin.
/// </summary>
/// <remarks>
/// Numeric values are part of the persistence contract. Renumbering is a
/// breaking change.
/// </remarks>
public enum TreasuryFeedTriggerKind
{
    /// <summary>Auto-fire from the nightly Quartz job.</summary>
    Scheduled = 0,

    /// <summary>Manually triggered by an admin via the admin REST surface.</summary>
    Manual = 1,
}

/// <summary>
/// R1810 / TOR BP 1.2-I — per-row outcome inside a Treasury feed import.
/// Captures whether the row produced a new receipt, updated an existing one,
/// was an idempotent no-op, or failed validation.
/// </summary>
/// <remarks>
/// Numeric values are part of the persistence contract. Renumbering is a
/// breaking change.
/// </remarks>
public enum TreasuryFeedImportRowStatus
{
    /// <summary>Row parsed; the importer has not yet attempted to project it.</summary>
    Pending = 0,

    /// <summary>Row produced a new <c>TreasuryPaymentReceipt</c> insert.</summary>
    Imported = 1,

    /// <summary>An existing receipt was found and updated in-place.</summary>
    Updated = 2,

    /// <summary>An existing receipt was found with identical content — no write occurred.</summary>
    Skipped = 3,

    /// <summary>Row failed validation or parsing; <c>ErrorCode</c> + <c>ErrorDescription</c> populated.</summary>
    Failed = 4,
}

/// <summary>
/// R1202 / TOR §3.4-C — biological sex of a beneficiary for whom a
/// capitalised-payment present-value computation is run. Drives the mortality-
/// table lookup (different life-expectancy series per sex) and is reflected on
/// the underlying <see cref="CapitalisedPaymentRequest"/> row.
/// </summary>
/// <remarks>
/// The system intentionally distinguishes biological sex (mortality-table
/// dimension) from civil-status / gender-identity attributes captured
/// elsewhere on a Solicitant profile. The mortality table is a published
/// statistical artefact keyed by biological sex, so we model it explicitly
/// here rather than reusing the broader civil-status enum.
/// </remarks>
public enum BeneficiarySex
{
    /// <summary>Male — drives the male life-expectancy column on the mortality table.</summary>
    Male = 0,

    /// <summary>Female — drives the female life-expectancy column on the mortality table.</summary>
    Female = 1,
}

/// <summary>
/// R1202 / TOR §3.4-C — lifecycle status of a capitalised-payment request
/// (cerere de capitalizare a plăților periodice). Drives the state-machine
/// guard at every <c>ICapitalisedPaymentService</c> entry point.
/// </summary>
public enum CapitalisedPaymentRequestStatus
{
    /// <summary>Operator is editing the request — no computation yet, no audit-trail commitment.</summary>
    Draft = 0,

    /// <summary>Request has been submitted and is waiting for the present-value computation.</summary>
    Submitted = 1,

    /// <summary>The orchestrator has picked up the request and is computing the present value.</summary>
    Computing = 2,

    /// <summary>Computation completed; a <see cref="CapitalisedPaymentDecision"/> row exists and awaits operator approval.</summary>
    ComputedAwaitingApproval = 3,

    /// <summary>Approved by a CNAS operator; the liquidator owes the capitalised amount to CNAS.</summary>
    Approved = 4,

    /// <summary>Rejected by a CNAS operator with a recorded rationale.</summary>
    Rejected = 5,

    /// <summary>Cancelled by the operator at any non-terminal stage; carries an explicit reason.</summary>
    Cancelled = 6,

    /// <summary>Liquidator has paid the capitalised amount and CNAS has booked the treasury receipt.</summary>
    Settled = 7,
}

/// <summary>
/// R1202 / TOR §3.4-C — kind of periodic obligation that the capitalised-
/// payment request converts into a single lump-sum settlement. The kind is
/// reflected on the decision metric so operators can chart per-obligation
/// volume and detect skew.
/// </summary>
public enum CapitalisedPaymentObligationKind
{
    /// <summary>Monthly incapacity-of-work indemnity owed to a former employee.</summary>
    IncapacityForWork = 0,

    /// <summary>Monthly loss-of-breadwinner indemnity owed to a surviving family member.</summary>
    LossOfBreadwinner = 1,

    /// <summary>Monthly occupational-disease indemnity owed to a former employee.</summary>
    OccupationalDisease = 2,
}

/// <summary>
/// R1202 / TOR §3.4-C — finalised decision outcome attached to a
/// capitalised-payment request after the computation completes. One row per
/// finalised computation; only Approved or Rejected values are persisted.
/// </summary>
public enum CapitalisedPaymentDecisionStatus
{
    /// <summary>The computed capitalised amount has been approved and is owed to CNAS.</summary>
    Approved = 0,

    /// <summary>The computation outcome was rejected by an operator with a recorded rationale.</summary>
    Rejected = 1,
}

/// <summary>
/// R2279 / TOR SEC 033 — origin of a <see cref="ClassificationCatalogSnapshot"/>
/// row. The trigger kind is reflected on the wire DTO + the snapshot metric
/// (<c>cnas.classification.snapshot_captured</c> tagged with <c>trigger_kind</c>).
/// </summary>
public enum ClassificationSnapshotTriggerKind
{
    /// <summary>Fired by the weekly <c>ClassificationCatalogSnapshotJob</c>.</summary>
    Scheduled = 0,

    /// <summary>Fired by an operator via the admin REST endpoint.</summary>
    Manual = 1,
}

/// <summary>
/// R2279 / TOR SEC 033 — lifecycle of a <see cref="ClassificationCatalogSnapshot"/>
/// row. A successful scan transitions <c>Capturing → Captured</c>; an unhandled
/// scanner fault flips the row to <c>Failed</c> with the reason persisted to
/// <see cref="ClassificationCatalogSnapshot.FailureReason"/>.
/// </summary>
public enum ClassificationSnapshotStatus
{
    /// <summary>Snapshot row inserted; scanner is iterating Contracts assemblies.</summary>
    Capturing = 0,

    /// <summary>Scanner finished; per-property entry rows are persisted.</summary>
    Captured = 1,

    /// <summary>Scanner threw — see <see cref="ClassificationCatalogSnapshot.FailureReason"/>.</summary>
    Failed = 2,
}

/// <summary>
/// R2279 / TOR SEC 033 — kind of drift detected between a baseline snapshot
/// and a current snapshot. Drift findings are persisted into
/// <c>ClassificationDriftFindings</c> so operators can chart drift trends and
/// acknowledge investigated rows without losing the audit trail.
/// </summary>
public enum ClassificationDriftKind
{
    /// <summary>A new classified property appeared in the current snapshot — flag for label review.</summary>
    Added = 0,

    /// <summary>A property present in the baseline is gone in the current snapshot — type renamed or removed.</summary>
    Removed = 1,

    /// <summary>The property exists in both snapshots but the sensitivity label changed.</summary>
    LabelChanged = 2,

    /// <summary>The property still exists but the explicit <c>[SensitivityClassification]</c> attribute was dropped.</summary>
    ClassificationLost = 3,
}

/// <summary>
/// R1403 / TOR §3.6-D — role under which a lifetime athlete-pension award is
/// granted. Drives both the eligibility-evaluator branch (different thresholds
/// for athletes vs coaches) and the amount-calculator branch (the coach 0.80
/// factor applies on top of the base multiplier when the role is
/// <see cref="Coach"/>).
/// </summary>
public enum AthletePensionRole
{
    /// <summary>The beneficiary is the high-performance athlete in person.</summary>
    Athlete = 0,

    /// <summary>The beneficiary is the coach who trained a medal-winning athlete.</summary>
    Coach = 1,
}

/// <summary>
/// R1403 / TOR §3.6-D — lifecycle status of an <c>AthletePensionAward</c>.
/// Drives the state-machine guard at every <c>IAthletePensionAwardService</c>
/// entry point.
/// </summary>
/// <remarks>
/// State transitions: <c>Draft → Submitted</c>; <c>Submitted → Approved |
/// Rejected</c>; <c>Approved → Active</c>; <c>Active → Suspended |
/// Terminated</c>; <c>Suspended → Active | Terminated</c>. <c>Rejected</c>
/// and <c>Terminated</c> are terminal. Invalid transitions return
/// <see cref="Cnas.Ps.Core.Common.ErrorCodes.Conflict"/> with the stable
/// message <c>ATHLETE_PENSION.INVALID_TRANSITION</c>.
/// </remarks>
public enum AthletePensionAwardStatus
{
    /// <summary>Operator is editing the award request — no commitment yet.</summary>
    Draft = 0,

    /// <summary>Request has been submitted and is awaiting an eligibility/approval decision.</summary>
    Submitted = 1,

    /// <summary>Approved by a CNAS operator; the monthly amount + multipliers are now snapshotted on the row.</summary>
    Approved = 2,

    /// <summary>Rejected by a CNAS operator with a recorded rationale. Terminal.</summary>
    Rejected = 3,

    /// <summary>The award is in payment; monthly disbursements proceed.</summary>
    Active = 4,

    /// <summary>The award is temporarily paused (e.g. while a re-verification is pending).</summary>
    Suspended = 5,

    /// <summary>The award has been terminated (e.g. on beneficiary death). Terminal.</summary>
    Terminated = 6,
}

/// <summary>
/// R1403 / TOR §3.6-D — kind of qualifying career achievement attached to an
/// <c>AthletePensionAward</c>. The medal tiers drive the base-multiplier
/// table on the amount calculator (e.g. Olympic gold → 250%); the
/// <see cref="CoachYearsService"/> sentinel carries a coach's total years of
/// service in the <c>Years</c> field of the parent record.
/// </summary>
public enum AthleteAchievementKind
{
    /// <summary>Olympic Games — gold medal.</summary>
    OlympicGold = 0,

    /// <summary>Olympic Games — silver medal.</summary>
    OlympicSilver = 1,

    /// <summary>Olympic Games — bronze medal.</summary>
    OlympicBronze = 2,

    /// <summary>World championship — gold medal.</summary>
    WorldChampionGold = 3,

    /// <summary>World championship — silver medal.</summary>
    WorldChampionSilver = 4,

    /// <summary>World championship — bronze medal.</summary>
    WorldChampionBronze = 5,

    /// <summary>European championship — gold medal.</summary>
    EuropeanChampionGold = 6,

    /// <summary>European championship — silver medal.</summary>
    EuropeanChampionSilver = 7,

    /// <summary>European championship — bronze medal.</summary>
    EuropeanChampionBronze = 8,

    /// <summary>World record set in an officially-sanctioned competition.</summary>
    WorldRecord = 9,

    /// <summary>European record set in an officially-sanctioned competition.</summary>
    EuropeanRecord = 10,

    /// <summary>Coach role only — accumulated years of professional service (years carried on the record).</summary>
    CoachYearsService = 11,
}

/// <summary>
/// R1201 / R1402 / TOR §3.4-B / §3.6-C — discriminator on the
/// reusable <see cref="IntlAgreementReviewCase"/> aggregate selecting which
/// benefit kind the 3-level international-agreements routing applies to.
/// </summary>
/// <remarks>
/// The enum is intentionally extensible — additional benefit kinds (e.g.
/// survivor pensions under bilateral agreements) will be added in later
/// iterations. Values are persisted as their stable enum-name strings.
/// </remarks>
public enum IntlAgreementBenefitKind
{
    /// <summary>R1201 / §3.4-B — sick-leave + maternity indemnities under bilateral agreements.</summary>
    IncapacityMaternity = 0,

    /// <summary>R1402 / §3.6-C — unemployment indemnity under bilateral agreements.</summary>
    Unemployment = 1,
}

/// <summary>
/// R1201 / R1402 — lifecycle state of an <see cref="IntlAgreementReviewCase"/>
/// as it traverses the 3-level routing chain (Local → Regional → National).
/// </summary>
/// <remarks>
/// Transitions:
/// <c>Draft → Submitted</c> (immediately advanced to <c>AtLocalReview</c>);
/// <c>AtLocalReview → AtRegionalReview | Rejected | RevisionRequested | Cancelled</c>;
/// <c>AtRegionalReview → AtNationalReview | Rejected | RevisionRequested | Cancelled</c>;
/// <c>AtNationalReview → Approved | Rejected | RevisionRequested | Cancelled</c>;
/// <c>RevisionRequested → AtLocalReview</c> (via resubmit) <c>| Cancelled</c>.
/// <c>Approved / Rejected / Cancelled</c> are terminal.
/// </remarks>
public enum IntlAgreementReviewCaseStatus
{
    /// <summary>Case created but not yet submitted to the routing chain.</summary>
    Draft = 0,

    /// <summary>Case has been submitted (transitional — promoted to AtLocalReview immediately).</summary>
    Submitted = 1,

    /// <summary>Case is sitting at the local-office reviewer queue.</summary>
    AtLocalReview = 2,

    /// <summary>Case has cleared local review and is sitting at the regional reviewer queue.</summary>
    AtRegionalReview = 3,

    /// <summary>Case has cleared regional review and is sitting at the national (CNAS HQ) reviewer queue.</summary>
    AtNationalReview = 4,

    /// <summary>All three levels approved — terminal success state.</summary>
    Approved = 5,

    /// <summary>A reviewer rejected the case — terminal failure state.</summary>
    Rejected = 6,

    /// <summary>A reviewer requested a revision; the case is back with the requester and may be resubmitted.</summary>
    RevisionRequested = 7,

    /// <summary>Operator cancelled the case before a terminal decision — terminal.</summary>
    Cancelled = 8,
}

/// <summary>
/// R1201 / R1402 — discrete routing level for an
/// <see cref="IntlAgreementReviewCase"/>. The first three values map to the
/// Local / Regional / National reviewer roles; <c>Complete</c> marks the
/// terminal-approved level and <c>RevisionRequired</c> marks the
/// awaiting-resubmit state.
/// </summary>
/// <remarks>
/// Only the first three values (<c>Local</c>, <c>Regional</c>, <c>National</c>)
/// are recorded on <see cref="IntlAgreementReviewStep"/> rows — the trailing
/// two values describe case-level state and never appear on step rows.
/// </remarks>
public enum IntlAgreementReviewLevel
{
    /// <summary>Local-office reviewer level — first stop in the chain.</summary>
    Local = 0,

    /// <summary>Regional-office reviewer level — second stop in the chain.</summary>
    Regional = 1,

    /// <summary>National (CNAS HQ) reviewer level — final stop in the chain.</summary>
    National = 2,

    /// <summary>Case-level state — all three levels have approved.</summary>
    Complete = 3,

    /// <summary>Case-level state — a reviewer requested a revision; awaiting requester resubmit.</summary>
    RevisionRequired = 4,
}

/// <summary>
/// R1201 / R1402 — single-level review outcome captured on an
/// <see cref="IntlAgreementReviewStep"/> row. The outcome drives the
/// next case-level transition: <c>Approved</c> promotes the case to the
/// next level (or to terminal <c>Approved</c> from National),
/// <c>Rejected</c> terminates the case, and <c>RevisionRequested</c> sends
/// it back to the requester.
/// </summary>
public enum IntlAgreementReviewStepOutcome
{
    /// <summary>Reviewer approved the case at this level — advance.</summary>
    Approved = 0,

    /// <summary>Reviewer rejected the case at this level — terminal failure.</summary>
    Rejected = 1,

    /// <summary>Reviewer requested a revision at this level — return to requester for resubmit.</summary>
    RevisionRequested = 2,
}

/// <summary>
/// R2271 / TOR SEC 025 — final access-control verdict produced by an ABAC rule. Used
/// both as the <see cref="Cnas.Ps.Core.Domain.AbacRule.Effect"/> of an individual rule
/// and as the <see cref="Cnas.Ps.Core.Domain.AbacRuleSet.DefaultEffect"/> applied when
/// no rule in the set matches.
/// </summary>
/// <remarks>
/// <para>
/// The vocabulary is intentionally minimal — a rule may grant access (<see cref="Allow"/>)
/// or refuse it (<see cref="Deny"/>). There is no "Indeterminate" state because a
/// rule that throws during evaluation is treated as "did not match" rather than as a
/// distinct effect (CLAUDE.md safe-by-default discipline — a malformed rule must not
/// silently grant access).
/// </para>
/// <para>
/// Stored as a stable enum-name string in the database so re-ordering values would
/// not silently flip an Allow rule to Deny.
/// </para>
/// </remarks>
public enum AbacEffect
{
    /// <summary>Grant access — the calling subject may perform the requested action.</summary>
    Allow = 0,

    /// <summary>Refuse access — the calling subject is forbidden from the requested action.</summary>
    Deny = 1,
}

/// <summary>
/// R2430 / R2431 / TOR M4 — origin of the source data stream consumed by a
/// <c>MigrationPlan</c>. Only the in-memory variant is wired in this
/// iteration; production adapters arrive in later iterations once the
/// legacy schema is finalised.
/// </summary>
/// <remarks>
/// Numeric values are part of the persistence contract — renumbering is a
/// breaking change.
/// </remarks>
public enum MigrationSourceKind
{
    /// <summary>Legacy Microsoft SQL Server source (placeholder — not yet wired).</summary>
    LegacySqlServer = 0,

    /// <summary>Legacy Oracle source (placeholder — not yet wired).</summary>
    LegacyOracle = 1,

    /// <summary>Operator-uploaded CSV extract (placeholder — not yet wired).</summary>
    Csv = 2,

    /// <summary>Test-only — fixture rows materialised by the in-memory implementation.</summary>
    InMemoryTest = 3,
}

/// <summary>
/// R2430 / TOR M4 — lifecycle status of a <c>MigrationPlan</c>. Plans are
/// authored as Draft, escalated to Approved after a second-administrator
/// review, then Activated when the dryRun + reconciliation chain
/// confirms shape, optionally Suspended for ad-hoc interventions, and
/// finally Archived once the legacy data is fully migrated.
/// </summary>
/// <remarks>
/// Numeric values are part of the persistence contract — renumbering is a
/// breaking change.
/// </remarks>
public enum MigrationPlanStatus
{
    /// <summary>Author-editable; cannot run yet.</summary>
    Draft = 0,

    /// <summary>Approved by a second admin; safe to activate.</summary>
    Approved = 1,

    /// <summary>Active — scheduled DryRun jobs may pick it up.</summary>
    Active = 2,

    /// <summary>Temporarily suspended; scheduled jobs ignore it until resumed.</summary>
    Suspended = 3,

    /// <summary>Terminal — historical only; no further runs are produced.</summary>
    Archived = 4,
}

/// <summary>
/// R2430 / TOR M4 — origin of the run that triggered a migration. Scheduled
/// runs come from the nightly Quartz job; manual + dryRun runs are
/// explicitly invoked by an admin.
/// </summary>
/// <remarks>
/// Numeric values are part of the persistence contract — renumbering is a
/// breaking change.
/// </remarks>
public enum MigrationTriggerKind
{
    /// <summary>Auto-fire from the nightly Quartz job (always DryRun in this iteration).</summary>
    Scheduled = 0,

    /// <summary>Manually triggered by an admin in Apply mode.</summary>
    Manual = 1,

    /// <summary>Manually triggered by an admin in DryRun mode (no commit).</summary>
    DryRun = 2,
}

/// <summary>
/// R2430 / TOR M4 — lifecycle status of a <c>MigrationRun</c>.
/// </summary>
/// <remarks>
/// Numeric values are part of the persistence contract — renumbering is a
/// breaking change.
/// </remarks>
public enum MigrationRunStatus
{
    /// <summary>Row inserted; the importer has not yet started streaming source records.</summary>
    Pending = 0,

    /// <summary>Importer is actively streaming + projecting source rows.</summary>
    Running = 1,

    /// <summary>All source rows processed without any failures.</summary>
    Completed = 2,

    /// <summary>All source rows processed but at least one row failed.</summary>
    CompletedWithErrors = 3,

    /// <summary>Aborted by an unrecoverable failure (source fetch, mapper crash, …).</summary>
    Failed = 4,

    /// <summary>Cancelled by an admin or by host-shutdown cancellation.</summary>
    Cancelled = 5,
}

/// <summary>
/// R2430 / R2433 / TOR M4 — severity of a finding produced during migration
/// mapping or reconciliation.
/// </summary>
/// <remarks>
/// Numeric values are part of the persistence contract — renumbering is a
/// breaking change.
/// </remarks>
public enum MigrationFindingSeverity
{
    /// <summary>Blocking issue — row is rejected and the run is marked CompletedWithErrors.</summary>
    Critical = 0,

    /// <summary>Non-blocking issue — row is still persisted but flagged for review.</summary>
    Warning = 1,

    /// <summary>Diagnostic note — neutral information emitted by the pipeline (e.g. unmapped passthrough).</summary>
    Info = 2,
}

/// <summary>
/// R2433 / TOR M4 — terminal outcome of a reconciliation run between the
/// source counter view and the staging-row counter view.
/// </summary>
/// <remarks>
/// Numeric values are part of the persistence contract — renumbering is a
/// breaking change.
/// </remarks>
public enum ReconciliationStatus
{
    /// <summary>Source and staging counts match within the configured tolerance.</summary>
    Passed = 0,

    /// <summary>Counts differ — the reconciliation report lists the deltas.</summary>
    Discrepancy = 1,

    /// <summary>The reconciliation itself failed to complete (source unreachable, mapper crash, …).</summary>
    Failed = 2,
}

/// <summary>
/// R2307 / TOR SEC 060 — the data-set scope a <c>BackupPolicy</c> targets.
/// </summary>
public enum BackupScope
{
    /// <summary>The primary OLTP PostgreSQL database hosting the operational tables.</summary>
    PrimaryDatabase = 0,

    /// <summary>File storage — MinIO / object-store bucket holding attachments + report artefacts.</summary>
    FileStorage = 1,

    /// <summary>Application log archives (audit, structured logs).</summary>
    Logs = 2,

    /// <summary>Encryption-key material — kept on a separate cadence per CLAUDE.md §5.7.</summary>
    EncryptionKeys = 3,
}

/// <summary>
/// R2307 / TOR SEC 060 — strategy the backup target uses to capture the payload.
/// </summary>
public enum BackupStrategy
{
    /// <summary>Full backup — entire data-set snapshot per fire.</summary>
    Full = 0,

    /// <summary>Incremental — only blocks changed since the previous Full or Incremental.</summary>
    Incremental = 1,

    /// <summary>Differential — only blocks changed since the previous Full backup.</summary>
    Differential = 2,
}

/// <summary>
/// R2307 / TOR SEC 060 — physical destination for a backup payload.
/// </summary>
public enum BackupTargetKind
{
    /// <summary>In-memory dictionary used by the test suite and as the default registration.</summary>
    InMemoryTest = 0,

    /// <summary>Local on-disk filesystem (operator-scoped path).</summary>
    LocalFilesystem = 1,

    /// <summary>S3-compatible object store (MinIO, AWS S3, MCloud).</summary>
    S3Compatible = 2,

    /// <summary>Azure Blob Storage.</summary>
    AzureBlob = 3,
}

/// <summary>
/// R2307 / TOR SEC 060 — lifecycle state of a single <c>BackupRun</c>.
/// </summary>
public enum BackupRunStatus
{
    /// <summary>Inserted but not yet started.</summary>
    Pending = 0,

    /// <summary>Currently producing + uploading the payload.</summary>
    Running = 1,

    /// <summary>Payload uploaded + integrity verified.</summary>
    Succeeded = 2,

    /// <summary>Upload or payload-production failed; row carries a sanitised reason.</summary>
    Failed = 3,

    /// <summary>Upload succeeded but the on-target hash differs from the local-computed hash.</summary>
    IntegrityFailed = 4,
}

/// <summary>
/// R2307 / TOR SEC 060 — origin of a <c>BackupRun</c>.
/// </summary>
public enum BackupTriggerKind
{
    /// <summary>Fired by the Quartz <c>BackupExecutionJob</c>.</summary>
    Scheduled = 0,

    /// <summary>Fired by an operator through the admin REST surface.</summary>
    Manual = 1,
}

/// <summary>
/// R2307 / TOR SEC 060 — outcome of a single integrity-check pass over a backup payload.
/// </summary>
public enum BackupIntegrityStatus
{
    /// <summary>Expected hash matches actual hash — payload is intact.</summary>
    Passed = 0,

    /// <summary>Expected hash differs from actual hash — payload is corrupt.</summary>
    Failed = 1,

    /// <summary>The check could not complete (payload missing, target unreachable).</summary>
    Inconclusive = 2,
}

/// <summary>
/// R2500 / TOR PIR 020-023 — operator-elevated severity of a support ticket.
/// The category-level default fans out at submit time; the operator can elevate
/// the severity later via <c>ISupportTicketService.EscalateAsync</c> or by
/// auto-escalation when an SLA breach fires.
/// </summary>
public enum SupportTicketSeverity
{
    /// <summary>Low — best-effort, no firm SLA target.</summary>
    Low = 0,

    /// <summary>Normal — the default category severity for routine requests.</summary>
    Normal = 1,

    /// <summary>High — significant impact; same-day resolution target.</summary>
    High = 2,

    /// <summary>Critical — production outage / security incident; minute-grade response.</summary>
    Critical = 3,
}

/// <summary>
/// R2500 / TOR PIR 020-023 — lifecycle state of a single
/// <c>SupportTicket</c>. The state machine is enforced strictly inside
/// <c>ISupportTicketService</c>; invalid transitions return
/// <c>Result.Failure(ErrorCodes.Conflict, "TICKET.INVALID_TRANSITION")</c>.
/// </summary>
public enum SupportTicketStatus
{
    /// <summary>The requester just submitted the ticket; nobody has triaged it yet.</summary>
    Submitted = 0,

    /// <summary>An operator acknowledged the ticket (first response). Stops the FirstResponse SLA.</summary>
    Acknowledged = 1,

    /// <summary>An operator is actively working the ticket.</summary>
    InProgress = 2,

    /// <summary>The operator handed the ticket back to the requester for additional information.</summary>
    WaitingOnRequester = 3,

    /// <summary>The operator has marked the ticket Resolved; awaiting requester confirmation / close.</summary>
    Resolved = 4,

    /// <summary>Closed — final terminal state once the requester (or auto-close sweep) confirms the resolution.</summary>
    Closed = 5,

    /// <summary>Cancelled — terminal state for tickets withdrawn before resolution.</summary>
    Cancelled = 6,

    /// <summary>Escalated — manually elevated, or auto-elevated by the SLA evaluator.</summary>
    Escalated = 7,
}

/// <summary>
/// R2500 / TOR PIR 020-023 — kind of SLA event recorded against a
/// <c>SupportTicket</c>. Captured by the SLA evaluator on the periodic sweep
/// and inserted into <c>SupportTicketSlaEvents</c> with idempotency on
/// <c>(TicketId, EventKind)</c>.
/// </summary>
public enum SupportTicketSlaEventKind
{
    /// <summary>The First-Response SLA window elapsed before the ticket was Acknowledged.</summary>
    FirstResponseBreached = 0,

    /// <summary>The Resolution SLA window elapsed before the ticket was Resolved or Closed.</summary>
    ResolutionBreached = 1,

    /// <summary>The ticket was escalated (auto or manual).</summary>
    Escalated = 2,

    /// <summary>The ticket was Acknowledged before its First-Response SLA window elapsed.</summary>
    FirstResponseMet = 3,

    /// <summary>The ticket was Resolved (or Closed) before its Resolution SLA window elapsed.</summary>
    ResolutionMet = 4,
}

/// <summary>
/// R2502 / TOR PIR 025 — classification of a maintenance window. Each kind has
/// a hard duration ceiling and a notice-lead-time requirement enforced inside
/// the <c>IMaintenanceWindowService</c> state machine:
/// <list type="bullet">
///   <item><description><see cref="Ordinary"/> — ≤ 4 hours, 5 business days advance notice.</description></item>
///   <item><description><see cref="Major"/> — ≤ 24 hours, 10 business days advance notice.</description></item>
///   <item><description><see cref="Urgent"/> — ≤ 2 hours, immediate notice (no minimum lead time).</description></item>
/// </list>
/// </summary>
public enum MaintenanceWindowKind
{
    /// <summary>Ordinary planned maintenance — max 4 hours, 5 business days notice.</summary>
    Ordinary = 0,

    /// <summary>Major planned maintenance — max 24 hours, 10 business days notice.</summary>
    Major = 1,

    /// <summary>Urgent maintenance — max 2 hours, no minimum lead time.</summary>
    Urgent = 2,
}

/// <summary>
/// R2502 / TOR PIR 025 — lifecycle state of a maintenance window. State
/// transitions are enforced strictly inside the
/// <c>IMaintenanceWindowService</c>; invalid transitions return
/// <c>Result.Failure(ErrorCodes.Conflict, "MAINT.INVALID_TRANSITION")</c>.
/// </summary>
public enum MaintenanceWindowStatus
{
    /// <summary>The window has been drafted but not yet announced to users.</summary>
    Draft = 0,

    /// <summary>The window's notice has been posted; the advance-notice clock is running.</summary>
    NoticePeriod = 1,

    /// <summary>An approver authorised the window; ready for execution.</summary>
    Approved = 2,

    /// <summary>The maintenance is being executed right now.</summary>
    InProgress = 3,

    /// <summary>The maintenance completed normally.</summary>
    Completed = 4,

    /// <summary>The window was cancelled before completion.</summary>
    Cancelled = 5,
}

/// <summary>
/// R2503 / TOR PIR 022-023 — cadence classification for a
/// <c>SystemUpdateSchedule</c>. The schedule's
/// <c>NoticeLeadTimeDays</c> field stores the advance-notice requirement
/// that R2504 enforces; the canonical defaults are documented on the
/// service contract.
/// </summary>
public enum UpdateCadenceKind
{
    /// <summary>Monthly maintenance update (default lead time: 30 days).</summary>
    Monthly = 0,

    /// <summary>Quarterly maintenance update (default lead time: 30 days).</summary>
    Quarterly = 1,

    /// <summary>Annual maintenance update (default lead time: 30 days).</summary>
    Annual = 2,

    /// <summary>Major version bump (default lead time: 180 days = 6 months).</summary>
    MajorVersion = 3,

    /// <summary>Critical bug-fix update (no minimum lead time; deployed as needed).</summary>
    Critical = 4,

    /// <summary>Security-patch update (no minimum lead time; deployed as needed).</summary>
    Security = 5,
}

/// <summary>
/// R2504 / TOR PIR 024 — lifecycle state of a
/// <c>SystemUpdateEvent</c>. State transitions are enforced strictly
/// inside the <c>ISystemUpdateEventService</c>; invalid transitions
/// return <c>Result.Failure(ErrorCodes.Conflict, "UPDATE.INVALID_TRANSITION")</c>.
/// </summary>
public enum SystemUpdateEventStatus
{
    /// <summary>The update has been planned but not yet announced.</summary>
    Planned = 0,

    /// <summary>The advance-notice has been delivered to CNAS.</summary>
    Notified = 1,

    /// <summary>The deployment is in progress.</summary>
    Deploying = 2,

    /// <summary>The deployment completed successfully.</summary>
    Deployed = 3,

    /// <summary>The planned update was cancelled.</summary>
    Cancelled = 4,
}

/// <summary>
/// R2505 / TOR PIR 030-033 — classification of a change request. Standard
/// changes are pre-approved patterns (e.g. routine config tweaks). Normal
/// changes require the full review/test-env/sign-code/approve flow.
/// Emergency changes still require the full audit trail but are expected to
/// fast-track through review during incident response.
/// </summary>
public enum ChangeRequestKind
{
    /// <summary>Pre-approved low-risk recurring change.</summary>
    Standard = 0,

    /// <summary>Default — must traverse the full four-eyes flow.</summary>
    Normal = 1,

    /// <summary>Incident-driven emergency change — full audit still required.</summary>
    Emergency = 2,
}

/// <summary>
/// R2505 / TOR PIR 030-033 — lifecycle state of a
/// <c>ChangeRequest</c>. State transitions are enforced strictly inside the
/// <c>IChangeRequestService</c>; invalid transitions return
/// <c>Result.Failure(ErrorCodes.Conflict, "CHG.INVALID_TRANSITION")</c>.
/// </summary>
public enum ChangeRequestStatus
{
    /// <summary>Draft saved by the requester; not yet submitted.</summary>
    Draft = 0,

    /// <summary>Submitted by the requester; pending intake.</summary>
    Submitted = 1,

    /// <summary>Picked up by a reviewer; under technical review.</summary>
    InReview = 2,

    /// <summary>Test-environment validation has been recorded by an operator distinct from the requester.</summary>
    TestEnvValidated = 3,

    /// <summary>The deployment artefact has been signed by an operator distinct from the requester and tester.</summary>
    CodeSigned = 4,

    /// <summary>An approver distinct from requester/tester/signer has authorised production deployment.</summary>
    ApprovedForProd = 5,

    /// <summary>The deployment is currently in progress.</summary>
    Deploying = 6,

    /// <summary>The deployment completed successfully.</summary>
    Deployed = 7,

    /// <summary>The change was rolled back after deployment.</summary>
    RolledBack = 8,

    /// <summary>The change was cancelled before reaching production.</summary>
    Cancelled = 9,
}

/// <summary>
/// R2505 / TOR PIR 030-033 — declared risk classification of a change. Set at
/// submission; informs the reviewer's vigilance level. Does not directly
/// influence the lifecycle, but is a search facet and a metric tag.
/// </summary>
public enum ChangeRequestRisk
{
    /// <summary>Low expected risk — local, reversible.</summary>
    Low = 0,

    /// <summary>Medium expected risk — broader impact or partial rollback.</summary>
    Medium = 1,

    /// <summary>High expected risk — production-impacting or hard-to-rollback.</summary>
    High = 2,
}

/// <summary>
/// R2506 / TOR PIR 037-040 — category of a quality risk identified by the QA
/// process. Used as a metric tag and a search facet.
/// </summary>
public enum QualityRiskCategory
{
    /// <summary>Technical risk — system, infrastructure, dependency.</summary>
    Technical = 0,

    /// <summary>Process risk — workflow, procedure, governance.</summary>
    Process = 1,

    /// <summary>Security risk — confidentiality, integrity, availability.</summary>
    Security = 2,

    /// <summary>Compliance risk — legal, regulatory, contractual.</summary>
    Compliance = 3,

    /// <summary>External risk — third-party, supplier, environmental.</summary>
    External = 4,

    /// <summary>People risk — staffing, training, key-person dependency.</summary>
    People = 5,
}

/// <summary>
/// R2506 / TOR PIR 037-040 — likelihood band on the standard 5-step
/// qualitative risk scale.
/// </summary>
public enum QualityRiskLikelihood
{
    /// <summary>Rare — unlikely to occur in any reasonable horizon.</summary>
    Rare = 0,

    /// <summary>Unlikely — could occur but not expected.</summary>
    Unlikely = 1,

    /// <summary>Possible — might occur at some point.</summary>
    Possible = 2,

    /// <summary>Likely — expected to occur.</summary>
    Likely = 3,

    /// <summary>AlmostCertain — strongly expected in the near term.</summary>
    AlmostCertain = 4,
}

/// <summary>
/// R2506 / TOR PIR 037-040 — impact band on the standard 5-step qualitative
/// risk scale.
/// </summary>
public enum QualityRiskImpact
{
    /// <summary>Minimal — negligible disruption.</summary>
    Minimal = 0,

    /// <summary>Minor — limited, isolated effect.</summary>
    Minor = 1,

    /// <summary>Moderate — noticeable but bounded effect.</summary>
    Moderate = 2,

    /// <summary>Major — significant business or operational effect.</summary>
    Major = 3,

    /// <summary>Severe — catastrophic effect on operations or compliance.</summary>
    Severe = 4,
}

/// <summary>
/// R2506 / TOR PIR 037-040 — lifecycle state of a quality risk. State
/// transitions are enforced strictly inside the
/// <c>IQualityRiskService</c>; invalid transitions return
/// <c>Result.Failure(ErrorCodes.Conflict, "QA_RISK.INVALID_TRANSITION")</c>.
/// </summary>
public enum QualityRiskStatus
{
    /// <summary>The risk has been identified but mitigation has not started.</summary>
    Open = 0,

    /// <summary>Mitigation actions are in progress.</summary>
    Mitigating = 1,

    /// <summary>The risk has been closed — either no longer relevant or mitigated.</summary>
    Closed = 2,

    /// <summary>The risk has been formally accepted by the responsible owner without further mitigation.</summary>
    Accepted = 3,
}

/// <summary>
/// R2506 / TOR PIR 037-040 — lifecycle state of a preventive action linked
/// to a <c>QualityRisk</c>.
/// </summary>
public enum QualityRiskActionStatus
{
    /// <summary>The action has been planned but work has not yet begun.</summary>
    Planned = 0,

    /// <summary>Work on the action is in progress.</summary>
    InProgress = 1,

    /// <summary>The action has been fully implemented.</summary>
    Implemented = 2,

    /// <summary>The action was cancelled before implementation.</summary>
    Cancelled = 3,
}

/// <summary>
/// R0103 / TOR CF 14.02 — outcome stamped on a
/// <see cref="ProcessedIntegrationEvent"/> row when the inbound CloudEvents
/// dispatcher records the receipt of an event MessageId. The enum is stored as
/// a string in PostgreSQL (via the EF Core configuration) so future values can
/// be added without a numeric-discriminator migration.
/// </summary>
public enum ProcessedEventOutcome
{
    /// <summary>
    /// The MessageId was seen for the first time and the downstream handler chain
    /// completed without raising an unhandled exception. The default outcome on
    /// successful claim.
    /// </summary>
    Accepted = 0,

    /// <summary>
    /// The MessageId arrived but the handler chose to skip it (no-op replay,
    /// schema-mismatch warn-only, etc.). Reserved for future per-handler use —
    /// the default deduper never writes this value itself.
    /// </summary>
    Skipped = 1,

    /// <summary>
    /// The MessageId was claimed but the downstream handler raised an unhandled
    /// exception. The dedup row is preserved (so retries still no-op) and a
    /// sanitised <c>FailureReason</c> is attached for ops forensics.
    /// </summary>
    Failed = 2,
}

/// <summary>
/// R0322 / TOR UI 014 — semantic category attached to an
/// <c>ApplicationAttachment</c> row. Distinct from the platform-wide
/// <see cref="AttachmentCategory"/> enum (R0227) because the application-attachment
/// vocabulary is dictated by the service-passport intake checklists in TOR Annex 3
/// (Birth / Death / Marriage / Custody certificates as required by family-benefit
/// services), whereas <see cref="AttachmentCategory"/> describes the generic
/// file-attachment widget surface.
/// </summary>
/// <remarks>
/// <para>
/// <b>Enum-value stability.</b> Numeric values are part of the persistence contract
/// (the column is stored as <c>int</c>); renumbering is a breaking change requiring
/// a data migration. Append new categories at the end.
/// </para>
/// </remarks>
public enum ApplicationAttachmentCategory
{
    /// <summary>Identity document — national id, passport, birth certificate (identity-confirming).</summary>
    Identity = 0,

    /// <summary>Income proof — salary statement, tax declaration, bank statement.</summary>
    Income = 1,

    /// <summary>Medical report — disability evaluation, medical commission decision.</summary>
    MedicalReport = 2,

    /// <summary>Birth certificate — required by maternity, child-allowance, family-benefit services.</summary>
    Birth = 3,

    /// <summary>Death certificate — required by survivor-pension, funeral-allowance services.</summary>
    Death = 4,

    /// <summary>Marriage certificate — required by spousal-benefit, family-status services.</summary>
    Marriage = 5,

    /// <summary>Custody / guardianship document — court decision, tutelage order.</summary>
    Custody = 6,

    /// <summary>Any attachment that does not fit a more specific category.</summary>
    Other = 99,
}

/// <summary>
/// R0322 / TOR UI 014 — virus-scan lifecycle state of an
/// <c>ApplicationAttachment</c> row. Each attachment is born <see cref="Pending"/>;
/// the scan worker flips it to one of the terminal values (<see cref="Clean"/>,
/// <see cref="Infected"/>, <see cref="ScanFailed"/>) or to <see cref="Skipped"/>
/// when scanning is administratively suppressed (e.g. internal migration imports).
/// </summary>
/// <remarks>
/// <para>
/// <b>Enum-value stability.</b> Numeric values are part of the persistence contract;
/// renumbering requires a data migration. Append new statuses at the end.
/// </para>
/// <para>
/// <b>Gate semantics.</b> Only <see cref="Clean"/> and <see cref="Skipped"/>
/// attachments are eligible for downstream consumption (decision pack rendering,
/// citizen download). <see cref="Pending"/> attachments are visible to the uploader
/// only; <see cref="Infected"/> / <see cref="ScanFailed"/> are quarantined.
/// </para>
/// </remarks>
public enum AttachmentVirusScanStatus
{
    /// <summary>Virus scan has not yet completed — default for newly-attached rows.</summary>
    Pending = 0,

    /// <summary>Scan completed; no malicious content detected. Downstream consumption is allowed.</summary>
    Clean = 1,

    /// <summary>Scan completed; malicious content detected. The attachment is quarantined.</summary>
    Infected = 2,

    /// <summary>Scan attempted but the engine failed (timeout, engine outage). Operator review required.</summary>
    ScanFailed = 3,

    /// <summary>Scan deliberately skipped — admin-only path for migration imports / internal sources.</summary>
    Skipped = 4,
}

/// <summary>
/// R0203 / TOR CF 20.06 — lifecycle status of an <c>ExternalSourceIngestionRun</c>.
/// </summary>
/// <remarks>
/// Numeric values are part of the persistence contract — renumbering is a breaking
/// change.
/// </remarks>
public enum ExternalSourceIngestionStatus
{
    /// <summary>Row inserted; the connector has not yet started fetching.</summary>
    Pending = 0,

    /// <summary>Connector is actively pulling + applying records.</summary>
    Running = 1,

    /// <summary>Every record processed (or skipped); counters are final.</summary>
    Completed = 2,

    /// <summary>Aborted by an unrecoverable failure (connector fetch, mapper crash, …).</summary>
    Failed = 3,

    /// <summary>Run intentionally skipped (peak-hour gate, missing configuration, …).</summary>
    Skipped = 4,
}

/// <summary>
/// R0203 / TOR CF 20.06 — origin of an <c>ExternalSourceIngestionRun</c>.
/// </summary>
/// <remarks>
/// Numeric values are part of the persistence contract — renumbering is a breaking
/// change.
/// </remarks>
public enum ExternalSourceTriggerKind
{
    /// <summary>Auto-fire from the per-source nightly Quartz job.</summary>
    Scheduled = 0,

    /// <summary>Manually triggered by an admin through the admin REST surface.</summary>
    Manual = 1,
}
