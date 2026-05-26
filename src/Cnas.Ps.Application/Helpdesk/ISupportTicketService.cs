using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Helpdesk;

/// <summary>
/// R2500 / TOR PIR 020-023 — helpdesk-ticket lifecycle service. Hosts the
/// strict state machine (Submitted → Acknowledged → InProgress →
/// WaitingOnRequester → Resolved → Closed / Cancelled / Escalated),
/// per-ticket audit emission, and the comment-timeline writer. Invalid
/// transitions return <c>Result.Failure(ErrorCodes.Conflict, "TICKET.INVALID_TRANSITION")</c>.
/// </summary>
public interface ISupportTicketService
{
    /// <summary>Stable failure code emitted when a transition is not legal for the ticket's current status.</summary>
    public const string InvalidTransitionCode = "TICKET.INVALID_TRANSITION";

    /// <summary>Stable failure code emitted when the caller is neither the requester nor an operator.</summary>
    public const string CallerNotAuthorisedCode = "TICKET.CALLER_NOT_AUTHORISED";

    /// <summary>Stable failure code emitted when reopen is attempted past the 7-day window.</summary>
    public const string ReopenWindowExpiredCode = "TICKET.REOPEN_WINDOW_EXPIRED";

    /// <summary>Stable audit event emitted on submit.</summary>
    public const string AuditSubmitted = "TICKET.SUBMITTED";

    /// <summary>Stable audit event emitted on acknowledge.</summary>
    public const string AuditAcknowledged = "TICKET.ACKNOWLEDGED";

    /// <summary>Stable audit event emitted on assign.</summary>
    public const string AuditAssigned = "TICKET.ASSIGNED";

    /// <summary>Stable audit event emitted on state transitions other than the special-cased ones.</summary>
    public const string AuditTransitioned = "TICKET.TRANSITIONED";

    /// <summary>Stable audit event emitted on escalate.</summary>
    public const string AuditEscalated = "TICKET.ESCALATED";

    /// <summary>Stable audit event emitted on resolve.</summary>
    public const string AuditResolved = "TICKET.RESOLVED";

    /// <summary>Stable audit event emitted on close.</summary>
    public const string AuditClosed = "TICKET.CLOSED";

    /// <summary>Stable audit event emitted on reopen.</summary>
    public const string AuditReopened = "TICKET.REOPENED";

    /// <summary>Stable audit event emitted on cancel.</summary>
    public const string AuditCancelled = "TICKET.CANCELLED";

    /// <summary>Stable audit event emitted when a comment is added.</summary>
    public const string AuditCommentAdded = "TICKET.COMMENT_ADDED";

    /// <summary>Reopen window in days (Resolved/Closed → InProgress).</summary>
    public const int ReopenWindowDays = 7;

    /// <summary>Submits a new helpdesk ticket; the caller becomes the requester.</summary>
    /// <param name="input">Submit payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The created ticket on success.</returns>
    Task<Result<SupportTicketDto>> SubmitAsync(
        SupportTicketSubmitInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>Acknowledges a Submitted ticket (operator first response).</summary>
    /// <param name="ticketSqid">Sqid-encoded ticket id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated ticket.</returns>
    Task<Result<SupportTicketDto>> AcknowledgeAsync(
        string ticketSqid,
        CancellationToken cancellationToken = default);

    /// <summary>Assigns the ticket to an operator with a free-form internal note.</summary>
    /// <param name="ticketSqid">Sqid-encoded ticket id.</param>
    /// <param name="input">Assign payload (assignee Sqid + note).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated ticket.</returns>
    Task<Result<SupportTicketDto>> AssignAsync(
        string ticketSqid,
        SupportTicketAssignInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>Transitions Acknowledged → InProgress.</summary>
    /// <param name="ticketSqid">Sqid-encoded ticket id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated ticket.</returns>
    Task<Result<SupportTicketDto>> StartProgressAsync(
        string ticketSqid,
        CancellationToken cancellationToken = default);

    /// <summary>Transitions InProgress → WaitingOnRequester with an internal note (recorded as a comment).</summary>
    /// <param name="ticketSqid">Sqid-encoded ticket id.</param>
    /// <param name="input">Reason payload (3..500 chars).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated ticket.</returns>
    Task<Result<SupportTicketDto>> RequestRequesterReplyAsync(
        string ticketSqid,
        SupportTicketReasonInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>Transitions WaitingOnRequester → InProgress.</summary>
    /// <param name="ticketSqid">Sqid-encoded ticket id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated ticket.</returns>
    Task<Result<SupportTicketDto>> ResumeFromRequesterAsync(
        string ticketSqid,
        CancellationToken cancellationToken = default);

    /// <summary>Escalates the ticket (any non-terminal state → Escalated). Critical audit.</summary>
    /// <param name="ticketSqid">Sqid-encoded ticket id.</param>
    /// <param name="input">Escalation reason payload (3..500 chars).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated ticket.</returns>
    Task<Result<SupportTicketDto>> EscalateAsync(
        string ticketSqid,
        SupportTicketReasonInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>Resolves the ticket (InProgress / WaitingOnRequester / Escalated → Resolved); requires a 3..2000-char summary.</summary>
    /// <param name="ticketSqid">Sqid-encoded ticket id.</param>
    /// <param name="input">Resolution-summary payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated ticket.</returns>
    Task<Result<SupportTicketDto>> ResolveAsync(
        string ticketSqid,
        SupportTicketResolutionInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>Closes a Resolved ticket.</summary>
    /// <param name="ticketSqid">Sqid-encoded ticket id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated ticket.</returns>
    Task<Result<SupportTicketDto>> CloseAsync(
        string ticketSqid,
        CancellationToken cancellationToken = default);

    /// <summary>Reopens a Resolved or Closed ticket (within the 7-day reopen window).</summary>
    /// <param name="ticketSqid">Sqid-encoded ticket id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated ticket.</returns>
    Task<Result<SupportTicketDto>> ReopenAsync(
        string ticketSqid,
        CancellationToken cancellationToken = default);

    /// <summary>Cancels any non-terminal ticket with a reason.</summary>
    /// <param name="ticketSqid">Sqid-encoded ticket id.</param>
    /// <param name="input">Cancellation reason payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated ticket.</returns>
    Task<Result<SupportTicketDto>> CancelAsync(
        string ticketSqid,
        SupportTicketReasonInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>Adds a comment to the ticket timeline (any non-terminal state).</summary>
    /// <param name="ticketSqid">Sqid-encoded ticket id.</param>
    /// <param name="input">Comment payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated ticket including the new comment.</returns>
    Task<Result<SupportTicketDto>> AddCommentAsync(
        string ticketSqid,
        SupportTicketCommentInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the ticket detail including comments + SLA events ordered chronologically.</summary>
    /// <param name="ticketSqid">Sqid-encoded ticket id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The ticket detail.</returns>
    Task<Result<SupportTicketDto>> GetByIdAsync(
        string ticketSqid,
        CancellationToken cancellationToken = default);

    /// <summary>Lists tickets (paged + filterable).</summary>
    /// <param name="filter">Filter envelope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The page on success.</returns>
    Task<Result<SupportTicketPageDto>> ListAsync(
        SupportTicketFilterDto filter,
        CancellationToken cancellationToken = default);
}
