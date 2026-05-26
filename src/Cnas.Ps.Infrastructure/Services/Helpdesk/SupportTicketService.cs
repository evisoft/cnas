using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Helpdesk;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.Helpdesk;

/// <summary>
/// R2500 / TOR PIR 020-023 — production implementation of
/// <see cref="ISupportTicketService"/>. Drives the helpdesk-ticket state
/// machine, computes per-ticket SLA deadlines at submit time, and emits a
/// stable per-transition audit + metric tuple.
/// </summary>
public sealed class SupportTicketService : ISupportTicketService
{
    /// <summary>Cached JSON serializer options shared across audit payloads.</summary>
    private static readonly JsonSerializerOptions CachedJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly ICnasDbContext _db;
    private readonly IReadOnlyCnasDbContext _read;
    private readonly ICnasTimeProvider _clock;
    private readonly ISqidService _sqids;
    private readonly ICallerContext _caller;
    private readonly IAuditService _audit;
    private readonly IValidator<SupportTicketSubmitInputDto> _submitValidator;
    private readonly IValidator<SupportTicketAssignInputDto> _assignValidator;
    private readonly IValidator<SupportTicketResolutionInputDto> _resolutionValidator;
    private readonly IValidator<SupportTicketReasonInputDto> _reasonValidator;
    private readonly IValidator<SupportTicketCommentInputDto> _commentValidator;
    private readonly IValidator<SupportTicketFilterDto> _filterValidator;

    /// <summary>Constructs the service with its scoped collaborators.</summary>
    /// <param name="db">Writer EF Core context.</param>
    /// <param name="read">Read-replica context.</param>
    /// <param name="clock">UTC clock abstraction.</param>
    /// <param name="sqids">Sqid encoder/decoder.</param>
    /// <param name="caller">Caller-context for audit attribution.</param>
    /// <param name="audit">Audit-journal façade.</param>
    /// <param name="submitValidator">Validator for submit input.</param>
    /// <param name="assignValidator">Validator for assign input.</param>
    /// <param name="resolutionValidator">Validator for resolve input.</param>
    /// <param name="reasonValidator">Validator for reason inputs (escalate / cancel / request-reply).</param>
    /// <param name="commentValidator">Validator for comment input.</param>
    /// <param name="filterValidator">Validator for list filter.</param>
    public SupportTicketService(
        ICnasDbContext db,
        IReadOnlyCnasDbContext read,
        ICnasTimeProvider clock,
        ISqidService sqids,
        ICallerContext caller,
        IAuditService audit,
        IValidator<SupportTicketSubmitInputDto> submitValidator,
        IValidator<SupportTicketAssignInputDto> assignValidator,
        IValidator<SupportTicketResolutionInputDto> resolutionValidator,
        IValidator<SupportTicketReasonInputDto> reasonValidator,
        IValidator<SupportTicketCommentInputDto> commentValidator,
        IValidator<SupportTicketFilterDto> filterValidator)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(submitValidator);
        ArgumentNullException.ThrowIfNull(assignValidator);
        ArgumentNullException.ThrowIfNull(resolutionValidator);
        ArgumentNullException.ThrowIfNull(reasonValidator);
        ArgumentNullException.ThrowIfNull(commentValidator);
        ArgumentNullException.ThrowIfNull(filterValidator);
        _db = db;
        _read = read;
        _clock = clock;
        _sqids = sqids;
        _caller = caller;
        _audit = audit;
        _submitValidator = submitValidator;
        _assignValidator = assignValidator;
        _resolutionValidator = resolutionValidator;
        _reasonValidator = reasonValidator;
        _commentValidator = commentValidator;
        _filterValidator = filterValidator;
    }

    /// <inheritdoc />
    public async Task<Result<SupportTicketDto>> SubmitAsync(
        SupportTicketSubmitInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var v = await _submitValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result<SupportTicketDto>.Failure(ErrorCodes.ValidationFailed, v.Errors[0].ErrorMessage);
        }
        if (_caller.UserId is null)
        {
            return Result<SupportTicketDto>.Failure(ErrorCodes.Unauthorized, "Anonymous callers cannot submit tickets.");
        }

        var category = await _db.SupportTicketCategories
            .FirstOrDefaultAsync(c => c.Code == input.CategoryCode, cancellationToken)
            .ConfigureAwait(false);
        if (category is null)
        {
            return Result<SupportTicketDto>.Failure(ErrorCodes.NotFound, $"Helpdesk category '{input.CategoryCode}' not found.");
        }
        if (!category.IsActive)
        {
            return Result<SupportTicketDto>.Failure(ErrorCodes.Conflict, $"Helpdesk category '{input.CategoryCode}' is not Active.");
        }

        var severity = input.Severity is null
            ? category.DefaultSeverity
            : Enum.Parse<SupportTicketSeverity>(input.Severity, ignoreCase: false);

        var now = _clock.UtcNow;
        var actor = _caller.UserSqid ?? "user";

        var ticketNumber = await MintTicketNumberAsync(now, cancellationToken).ConfigureAwait(false);

        var ticket = new SupportTicket
        {
            TicketNumber = ticketNumber,
            CategoryId = category.Id,
            Title = input.Title,
            Description = input.Description,
            Severity = severity,
            Status = SupportTicketStatus.Submitted,
            SubmittedByUserId = _caller.UserId.Value,
            SubmittedAt = now,
            FirstResponseDueAt = now.AddMinutes(category.FirstResponseSlaMinutes),
            ResolutionDueAt = now.AddMinutes(category.ResolutionSlaMinutes),
            CreatedAtUtc = now,
            CreatedBy = actor,
            IsActive = true,
        };
        _db.SupportTickets.Add(ticket);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        CnasMeter.SupportTicketSubmitted.Add(
            1,
            new KeyValuePair<string, object?>("category_code", category.Code),
            new KeyValuePair<string, object?>("severity", severity.ToString()));

        await EmitAuditAsync(
            ISupportTicketService.AuditSubmitted,
            AuditSeverity.Notice,
            actor,
            ticket.Id,
            new
            {
                ticketSqid = _sqids.Encode(ticket.Id),
                ticketNumber = ticket.TicketNumber,
                categoryCode = category.Code,
                severity = severity.ToString(),
            },
            cancellationToken).ConfigureAwait(false);

        return await ProjectAsync(ticket, category, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result<SupportTicketDto>> AcknowledgeAsync(
        string ticketSqid,
        CancellationToken cancellationToken = default)
    {
        var loaded = await LoadAsync(ticketSqid, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return Result<SupportTicketDto>.Failure(loaded.ErrorCode!, loaded.ErrorMessage!);
        }
        var ticket = loaded.Value;
        if (ticket.Status != SupportTicketStatus.Submitted)
        {
            return Result<SupportTicketDto>.Failure(
                ISupportTicketService.InvalidTransitionCode,
                $"Cannot acknowledge: current status is {ticket.Status}.");
        }
        var from = ticket.Status;
        var now = _clock.UtcNow;
        ticket.Status = SupportTicketStatus.Acknowledged;
        ticket.FirstAcknowledgedAt = now;
        StampUpdated(ticket, now);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await AuditTransitionAsync(ticket, from, ISupportTicketService.AuditAcknowledged, AuditSeverity.Notice, cancellationToken).ConfigureAwait(false);
        return await ProjectAsync(ticket, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result<SupportTicketDto>> AssignAsync(
        string ticketSqid,
        SupportTicketAssignInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var v = await _assignValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result<SupportTicketDto>.Failure(ErrorCodes.ValidationFailed, v.Errors[0].ErrorMessage);
        }
        var assigneeDecoded = _sqids.TryDecode(input.AssignedToUserSqid);
        if (assigneeDecoded.IsFailure)
        {
            return Result<SupportTicketDto>.Failure(assigneeDecoded.ErrorCode!, assigneeDecoded.ErrorMessage!);
        }

        var loaded = await LoadAsync(ticketSqid, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return Result<SupportTicketDto>.Failure(loaded.ErrorCode!, loaded.ErrorMessage!);
        }
        var ticket = loaded.Value;
        if (IsTerminal(ticket.Status))
        {
            return Result<SupportTicketDto>.Failure(
                ISupportTicketService.InvalidTransitionCode,
                $"Cannot assign a ticket in terminal status {ticket.Status}.");
        }

        var now = _clock.UtcNow;
        ticket.AssignedToUserId = assigneeDecoded.Value;
        StampUpdated(ticket, now);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var actor = _caller.UserSqid ?? "operator";
        await EmitAuditAsync(
            ISupportTicketService.AuditAssigned,
            AuditSeverity.Notice,
            actor,
            ticket.Id,
            new
            {
                ticketSqid = _sqids.Encode(ticket.Id),
                ticketNumber = ticket.TicketNumber,
                assignedToUserSqid = input.AssignedToUserSqid,
            },
            cancellationToken).ConfigureAwait(false);

        return await ProjectAsync(ticket, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result<SupportTicketDto>> StartProgressAsync(
        string ticketSqid,
        CancellationToken cancellationToken = default)
    {
        var loaded = await LoadAsync(ticketSqid, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return Result<SupportTicketDto>.Failure(loaded.ErrorCode!, loaded.ErrorMessage!);
        }
        var ticket = loaded.Value;
        if (ticket.Status != SupportTicketStatus.Acknowledged)
        {
            return Result<SupportTicketDto>.Failure(
                ISupportTicketService.InvalidTransitionCode,
                $"Cannot start progress: current status is {ticket.Status}.");
        }
        return await ApplyStatusTransitionAsync(ticket, SupportTicketStatus.InProgress, ISupportTicketService.AuditTransitioned, AuditSeverity.Notice, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result<SupportTicketDto>> RequestRequesterReplyAsync(
        string ticketSqid,
        SupportTicketReasonInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var v = await _reasonValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result<SupportTicketDto>.Failure(ErrorCodes.ValidationFailed, v.Errors[0].ErrorMessage);
        }
        var loaded = await LoadAsync(ticketSqid, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return Result<SupportTicketDto>.Failure(loaded.ErrorCode!, loaded.ErrorMessage!);
        }
        var ticket = loaded.Value;
        if (ticket.Status != SupportTicketStatus.InProgress)
        {
            return Result<SupportTicketDto>.Failure(
                ISupportTicketService.InvalidTransitionCode,
                $"Cannot request requester reply: current status is {ticket.Status}.");
        }
        // Add the reason as a non-internal comment so the requester can read it,
        // then transition.
        var now = _clock.UtcNow;
        AddCommentRow(ticket, input.Reason, isInternalOnly: false, now);
        return await ApplyStatusTransitionAsync(ticket, SupportTicketStatus.WaitingOnRequester, ISupportTicketService.AuditTransitioned, AuditSeverity.Notice, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result<SupportTicketDto>> ResumeFromRequesterAsync(
        string ticketSqid,
        CancellationToken cancellationToken = default)
    {
        var loaded = await LoadAsync(ticketSqid, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return Result<SupportTicketDto>.Failure(loaded.ErrorCode!, loaded.ErrorMessage!);
        }
        var ticket = loaded.Value;
        if (ticket.Status != SupportTicketStatus.WaitingOnRequester)
        {
            return Result<SupportTicketDto>.Failure(
                ISupportTicketService.InvalidTransitionCode,
                $"Cannot resume from requester: current status is {ticket.Status}.");
        }
        return await ApplyStatusTransitionAsync(ticket, SupportTicketStatus.InProgress, ISupportTicketService.AuditTransitioned, AuditSeverity.Notice, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result<SupportTicketDto>> EscalateAsync(
        string ticketSqid,
        SupportTicketReasonInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var v = await _reasonValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result<SupportTicketDto>.Failure(ErrorCodes.ValidationFailed, v.Errors[0].ErrorMessage);
        }
        var loaded = await LoadAsync(ticketSqid, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return Result<SupportTicketDto>.Failure(loaded.ErrorCode!, loaded.ErrorMessage!);
        }
        var ticket = loaded.Value;
        if (IsTerminal(ticket.Status))
        {
            return Result<SupportTicketDto>.Failure(
                ISupportTicketService.InvalidTransitionCode,
                $"Cannot escalate a ticket in terminal status {ticket.Status}.");
        }
        var from = ticket.Status;
        var now = _clock.UtcNow;
        ticket.Status = SupportTicketStatus.Escalated;
        ticket.EscalatedAt = now;
        ticket.EscalationReason = input.Reason;
        StampUpdated(ticket, now);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await AuditTransitionAsync(ticket, from, ISupportTicketService.AuditEscalated, AuditSeverity.Critical, cancellationToken).ConfigureAwait(false);
        return await ProjectAsync(ticket, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result<SupportTicketDto>> ResolveAsync(
        string ticketSqid,
        SupportTicketResolutionInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var v = await _resolutionValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result<SupportTicketDto>.Failure(ErrorCodes.ValidationFailed, v.Errors[0].ErrorMessage);
        }
        var loaded = await LoadAsync(ticketSqid, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return Result<SupportTicketDto>.Failure(loaded.ErrorCode!, loaded.ErrorMessage!);
        }
        var ticket = loaded.Value;
        if (ticket.Status != SupportTicketStatus.InProgress
            && ticket.Status != SupportTicketStatus.WaitingOnRequester
            && ticket.Status != SupportTicketStatus.Escalated)
        {
            return Result<SupportTicketDto>.Failure(
                ISupportTicketService.InvalidTransitionCode,
                $"Cannot resolve from status {ticket.Status}.");
        }
        var from = ticket.Status;
        var now = _clock.UtcNow;
        ticket.Status = SupportTicketStatus.Resolved;
        ticket.ResolvedAt = now;
        ticket.ResolutionSummary = input.Summary;
        StampUpdated(ticket, now);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await AuditTransitionAsync(ticket, from, ISupportTicketService.AuditResolved, AuditSeverity.Notice, cancellationToken).ConfigureAwait(false);
        return await ProjectAsync(ticket, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result<SupportTicketDto>> CloseAsync(
        string ticketSqid,
        CancellationToken cancellationToken = default)
    {
        var loaded = await LoadAsync(ticketSqid, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return Result<SupportTicketDto>.Failure(loaded.ErrorCode!, loaded.ErrorMessage!);
        }
        var ticket = loaded.Value;
        if (ticket.Status != SupportTicketStatus.Resolved)
        {
            return Result<SupportTicketDto>.Failure(
                ISupportTicketService.InvalidTransitionCode,
                $"Cannot close from status {ticket.Status}.");
        }
        var from = ticket.Status;
        var now = _clock.UtcNow;
        ticket.Status = SupportTicketStatus.Closed;
        ticket.ClosedAt = now;
        StampUpdated(ticket, now);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await AuditTransitionAsync(ticket, from, ISupportTicketService.AuditClosed, AuditSeverity.Notice, cancellationToken).ConfigureAwait(false);
        return await ProjectAsync(ticket, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result<SupportTicketDto>> ReopenAsync(
        string ticketSqid,
        CancellationToken cancellationToken = default)
    {
        var loaded = await LoadAsync(ticketSqid, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return Result<SupportTicketDto>.Failure(loaded.ErrorCode!, loaded.ErrorMessage!);
        }
        var ticket = loaded.Value;
        if (ticket.Status != SupportTicketStatus.Resolved
            && ticket.Status != SupportTicketStatus.Closed)
        {
            return Result<SupportTicketDto>.Failure(
                ISupportTicketService.InvalidTransitionCode,
                $"Cannot reopen from status {ticket.Status}.");
        }
        // Window: 7 days since whichever terminal stamp was most recently set.
        var anchor = ticket.ClosedAt ?? ticket.ResolvedAt ?? ticket.SubmittedAt;
        var now = _clock.UtcNow;
        if (now - anchor > TimeSpan.FromDays(ISupportTicketService.ReopenWindowDays))
        {
            return Result<SupportTicketDto>.Failure(
                ISupportTicketService.ReopenWindowExpiredCode,
                $"Reopen window of {ISupportTicketService.ReopenWindowDays} days has expired.");
        }

        var from = ticket.Status;
        ticket.Status = SupportTicketStatus.InProgress;
        ticket.ClosedAt = null;
        ticket.ResolvedAt = null;
        ticket.ResolutionSummary = null;
        StampUpdated(ticket, now);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await AuditTransitionAsync(ticket, from, ISupportTicketService.AuditReopened, AuditSeverity.Notice, cancellationToken).ConfigureAwait(false);
        return await ProjectAsync(ticket, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result<SupportTicketDto>> CancelAsync(
        string ticketSqid,
        SupportTicketReasonInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var v = await _reasonValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result<SupportTicketDto>.Failure(ErrorCodes.ValidationFailed, v.Errors[0].ErrorMessage);
        }
        var loaded = await LoadAsync(ticketSqid, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return Result<SupportTicketDto>.Failure(loaded.ErrorCode!, loaded.ErrorMessage!);
        }
        var ticket = loaded.Value;
        if (IsTerminal(ticket.Status))
        {
            return Result<SupportTicketDto>.Failure(
                ISupportTicketService.InvalidTransitionCode,
                $"Cannot cancel a ticket in terminal status {ticket.Status}.");
        }
        var from = ticket.Status;
        var now = _clock.UtcNow;
        ticket.Status = SupportTicketStatus.Cancelled;
        ticket.CancelReason = input.Reason;
        StampUpdated(ticket, now);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await AuditTransitionAsync(ticket, from, ISupportTicketService.AuditCancelled, AuditSeverity.Notice, cancellationToken).ConfigureAwait(false);
        return await ProjectAsync(ticket, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result<SupportTicketDto>> AddCommentAsync(
        string ticketSqid,
        SupportTicketCommentInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var v = await _commentValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result<SupportTicketDto>.Failure(ErrorCodes.ValidationFailed, v.Errors[0].ErrorMessage);
        }
        if (_caller.UserId is null)
        {
            return Result<SupportTicketDto>.Failure(ErrorCodes.Unauthorized, "Anonymous callers cannot post comments.");
        }
        var loaded = await LoadAsync(ticketSqid, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return Result<SupportTicketDto>.Failure(loaded.ErrorCode!, loaded.ErrorMessage!);
        }
        var ticket = loaded.Value;
        if (IsTerminal(ticket.Status))
        {
            return Result<SupportTicketDto>.Failure(
                ISupportTicketService.InvalidTransitionCode,
                $"Cannot comment on a ticket in terminal status {ticket.Status}.");
        }
        var now = _clock.UtcNow;
        AddCommentRow(ticket, input.Body, input.IsInternalOnly, now);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var actor = _caller.UserSqid ?? "user";
        // PII-discipline: audit references the ticket sqid + author only, never the body.
        await EmitAuditAsync(
            ISupportTicketService.AuditCommentAdded,
            AuditSeverity.Information,
            actor,
            ticket.Id,
            new
            {
                ticketSqid = _sqids.Encode(ticket.Id),
                ticketNumber = ticket.TicketNumber,
                isInternalOnly = input.IsInternalOnly,
            },
            cancellationToken).ConfigureAwait(false);

        return await ProjectAsync(ticket, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result<SupportTicketDto>> GetByIdAsync(
        string ticketSqid,
        CancellationToken cancellationToken = default)
    {
        var decoded = _sqids.TryDecode(ticketSqid);
        if (decoded.IsFailure)
        {
            return Result<SupportTicketDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var ticket = await _read.SupportTickets
            .FirstOrDefaultAsync(t => t.Id == decoded.Value, cancellationToken)
            .ConfigureAwait(false);
        if (ticket is null)
        {
            return Result<SupportTicketDto>.Failure(ErrorCodes.NotFound, "Support ticket not found.");
        }
        return await ProjectAsync(ticket, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result<SupportTicketPageDto>> ListAsync(
        SupportTicketFilterDto filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var v = await _filterValidator.ValidateAsync(filter, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result<SupportTicketPageDto>.Failure(ErrorCodes.ValidationFailed, v.Errors[0].ErrorMessage);
        }

        IQueryable<SupportTicket> q = _read.SupportTickets;
        if (!string.IsNullOrWhiteSpace(filter.Status)
            && Enum.TryParse<SupportTicketStatus>(filter.Status, ignoreCase: false, out var status))
        {
            q = q.Where(t => t.Status == status);
        }
        if (!string.IsNullOrWhiteSpace(filter.Severity)
            && Enum.TryParse<SupportTicketSeverity>(filter.Severity, ignoreCase: false, out var severity))
        {
            q = q.Where(t => t.Severity == severity);
        }
        if (!string.IsNullOrWhiteSpace(filter.CategoryCode))
        {
            var code = filter.CategoryCode;
            var categoryId = await _read.SupportTicketCategories
                .Where(c => c.Code == code)
                .Select(c => (long?)c.Id)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (categoryId is null)
            {
                return Result<SupportTicketPageDto>.Success(new SupportTicketPageDto(Array.Empty<SupportTicketDto>(), 0, filter.Skip, filter.Take));
            }
            var cid = categoryId.Value;
            q = q.Where(t => t.CategoryId == cid);
        }
        if (!string.IsNullOrWhiteSpace(filter.SubmittedByUserSqid))
        {
            var decoded = _sqids.TryDecode(filter.SubmittedByUserSqid);
            if (decoded.IsFailure)
            {
                return Result<SupportTicketPageDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
            }
            var uid = decoded.Value;
            q = q.Where(t => t.SubmittedByUserId == uid);
        }
        if (!string.IsNullOrWhiteSpace(filter.AssignedToUserSqid))
        {
            var decoded = _sqids.TryDecode(filter.AssignedToUserSqid);
            if (decoded.IsFailure)
            {
                return Result<SupportTicketPageDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
            }
            var uid = decoded.Value;
            q = q.Where(t => t.AssignedToUserId == uid);
        }

        var total = await q.CountAsync(cancellationToken).ConfigureAwait(false);
        var rows = await q
            .OrderByDescending(t => t.SubmittedAt)
            .ThenByDescending(t => t.Id)
            .Skip(filter.Skip)
            .Take(filter.Take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var items = new List<SupportTicketDto>(rows.Count);
        foreach (var row in rows)
        {
            var projected = await ProjectAsync(row, cancellationToken).ConfigureAwait(false);
            if (projected.IsSuccess)
            {
                items.Add(projected.Value);
            }
        }
        return Result<SupportTicketPageDto>.Success(new SupportTicketPageDto(items, total, filter.Skip, filter.Take));
    }

    /// <summary>Returns the canonical chronological-order ticket projection.</summary>
    /// <param name="ticket">Loaded ticket.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Outbound DTO.</returns>
    private async Task<Result<SupportTicketDto>> ProjectAsync(SupportTicket ticket, CancellationToken cancellationToken)
    {
        var category = await _read.SupportTicketCategories
            .FirstOrDefaultAsync(c => c.Id == ticket.CategoryId, cancellationToken)
            .ConfigureAwait(false);
        return await ProjectAsync(ticket, category, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Returns the canonical chronological-order ticket projection (category pre-loaded).</summary>
    /// <param name="ticket">Loaded ticket.</param>
    /// <param name="category">Loaded category (or null when missing).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Outbound DTO.</returns>
    private async Task<Result<SupportTicketDto>> ProjectAsync(SupportTicket ticket, SupportTicketCategory? category, CancellationToken cancellationToken)
    {
        var comments = await _read.SupportTicketComments
            .Where(c => c.TicketId == ticket.Id)
            .OrderBy(c => c.PostedAt)
            .ThenBy(c => c.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var slaEvents = await _read.SupportTicketSlaEvents
            .Where(e => e.TicketId == ticket.Id)
            .OrderBy(e => e.DetectedAt)
            .ThenBy(e => e.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var dto = new SupportTicketDto(
            Id: _sqids.Encode(ticket.Id),
            TicketNumber: ticket.TicketNumber,
            CategoryCode: category?.Code ?? string.Empty,
            Title: ticket.Title,
            Description: ticket.Description,
            Severity: ticket.Severity.ToString(),
            Status: ticket.Status.ToString(),
            SubmittedByUserSqid: _sqids.Encode(ticket.SubmittedByUserId),
            AssignedToUserSqid: ticket.AssignedToUserId is null ? null : _sqids.Encode(ticket.AssignedToUserId.Value),
            SubmittedAt: ticket.SubmittedAt,
            FirstAcknowledgedAt: ticket.FirstAcknowledgedAt,
            ResolvedAt: ticket.ResolvedAt,
            ClosedAt: ticket.ClosedAt,
            FirstResponseDueAt: ticket.FirstResponseDueAt,
            ResolutionDueAt: ticket.ResolutionDueAt,
            EscalatedAt: ticket.EscalatedAt,
            EscalationReason: ticket.EscalationReason,
            ResolutionSummary: ticket.ResolutionSummary,
            CancelReason: ticket.CancelReason,
            Comments: comments.Select(c => new SupportTicketCommentDto(
                Id: _sqids.Encode(c.Id),
                AuthorUserSqid: _sqids.Encode(c.AuthorUserId),
                Body: c.Body,
                IsInternalOnly: c.IsInternalOnly,
                PostedAt: c.PostedAt)).ToList(),
            SlaEvents: slaEvents.Select(e => new SupportTicketSlaEventDto(
                Id: _sqids.Encode(e.Id),
                EventKind: e.EventKind.ToString(),
                DetectedAt: e.DetectedAt,
                Notes: e.Notes)).ToList());
        return Result<SupportTicketDto>.Success(dto);
    }

    /// <summary>Returns true when the ticket is in a terminal state (Closed / Cancelled).</summary>
    /// <param name="status">Current ticket status.</param>
    /// <returns>True when no further transitions are permitted.</returns>
    private static bool IsTerminal(SupportTicketStatus status)
        => status == SupportTicketStatus.Closed || status == SupportTicketStatus.Cancelled;

    /// <summary>Loads a ticket by Sqid with friendly failures.</summary>
    /// <param name="ticketSqid">Sqid-encoded ticket id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The loaded entity on success.</returns>
    private async Task<Result<SupportTicket>> LoadAsync(string ticketSqid, CancellationToken cancellationToken)
    {
        var decoded = _sqids.TryDecode(ticketSqid);
        if (decoded.IsFailure)
        {
            return Result<SupportTicket>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var row = await _db.SupportTickets
            .FirstOrDefaultAsync(t => t.Id == decoded.Value, cancellationToken)
            .ConfigureAwait(false);
        return row is null
            ? Result<SupportTicket>.Failure(ErrorCodes.NotFound, "Support ticket not found.")
            : Result<SupportTicket>.Success(row);
    }

    /// <summary>
    /// Generic helper for status-only transitions (no extra metadata). Stamps
    /// updated-at + saves, then emits a transition audit row.
    /// </summary>
    /// <param name="ticket">Loaded ticket.</param>
    /// <param name="toStatus">Target status.</param>
    /// <param name="auditEvent">Audit event code.</param>
    /// <param name="severity">Audit severity.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated DTO on success.</returns>
    private async Task<Result<SupportTicketDto>> ApplyStatusTransitionAsync(
        SupportTicket ticket,
        SupportTicketStatus toStatus,
        string auditEvent,
        AuditSeverity severity,
        CancellationToken cancellationToken)
    {
        var from = ticket.Status;
        var now = _clock.UtcNow;
        ticket.Status = toStatus;
        StampUpdated(ticket, now);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await AuditTransitionAsync(ticket, from, auditEvent, severity, cancellationToken).ConfigureAwait(false);
        return await ProjectAsync(ticket, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Emits a transition audit row + bumps the state-change metric.</summary>
    /// <param name="ticket">Loaded ticket (after the transition).</param>
    /// <param name="from">Status before the transition.</param>
    /// <param name="auditEvent">Audit event code.</param>
    /// <param name="severity">Audit severity.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task AuditTransitionAsync(
        SupportTicket ticket,
        SupportTicketStatus from,
        string auditEvent,
        AuditSeverity severity,
        CancellationToken cancellationToken)
    {
        var actor = _caller.UserSqid ?? "user";
        var category = await _read.SupportTicketCategories
            .Where(c => c.Id == ticket.CategoryId)
            .Select(c => c.Code)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false) ?? "UNKNOWN";

        CnasMeter.SupportTicketStateChanged.Add(
            1,
            new KeyValuePair<string, object?>("category_code", category),
            new KeyValuePair<string, object?>("from_status", from.ToString()),
            new KeyValuePair<string, object?>("to_status", ticket.Status.ToString()));

        await EmitAuditAsync(
            auditEvent,
            severity,
            actor,
            ticket.Id,
            new
            {
                ticketSqid = _sqids.Encode(ticket.Id),
                ticketNumber = ticket.TicketNumber,
                fromStatus = from.ToString(),
                toStatus = ticket.Status.ToString(),
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Appends a comment row in memory (caller is responsible for SaveChanges).</summary>
    /// <param name="ticket">Parent ticket.</param>
    /// <param name="body">Comment body.</param>
    /// <param name="isInternalOnly">Internal-only flag.</param>
    /// <param name="now">UTC timestamp.</param>
    private void AddCommentRow(SupportTicket ticket, string body, bool isInternalOnly, DateTime now)
    {
        var actor = _caller.UserSqid ?? "user";
        var comment = new SupportTicketComment
        {
            TicketId = ticket.Id,
            AuthorUserId = _caller.UserId ?? 0,
            Body = body,
            IsInternalOnly = isInternalOnly,
            PostedAt = now,
            CreatedAtUtc = now,
            CreatedBy = actor,
            IsActive = true,
        };
        _db.SupportTicketComments.Add(comment);
    }

    /// <summary>Stamps UpdatedAtUtc + UpdatedBy on the supplied ticket.</summary>
    /// <param name="ticket">Loaded ticket.</param>
    /// <param name="now">Current UTC timestamp.</param>
    private void StampUpdated(SupportTicket ticket, DateTime now)
    {
        ticket.UpdatedAtUtc = now;
        ticket.UpdatedBy = _caller.UserSqid ?? "user";
    }

    /// <summary>Generates the deterministic <c>TKT-{year}-{seq:000000}</c> ticket number.</summary>
    /// <param name="now">Current UTC timestamp.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The next ticket number for the current year.</returns>
    private async Task<string> MintTicketNumberAsync(DateTime now, CancellationToken cancellationToken)
    {
        var year = now.Year;
        var yearPrefix = $"TKT-{year}-";
        var sequence = await _db.SupportTickets
            .Where(t => t.TicketNumber.StartsWith(yearPrefix))
            .CountAsync(cancellationToken).ConfigureAwait(false) + 1;
        return string.Create(CultureInfo.InvariantCulture, $"{yearPrefix}{sequence:D6}");
    }

    /// <summary>Writes a single audit row with a serialised details payload.</summary>
    /// <param name="eventCode">Stable event code.</param>
    /// <param name="severity">Audit severity.</param>
    /// <param name="actor">Audit-attribution string.</param>
    /// <param name="targetEntityId">Database id of the affected row.</param>
    /// <param name="details">Arbitrary anonymous object serialised to JSON.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Completes when the audit row is enqueued.</returns>
    private async Task EmitAuditAsync(
        string eventCode,
        AuditSeverity severity,
        string actor,
        long targetEntityId,
        object details,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(details, CachedJsonOptions);
        await _audit.RecordAsync(
            eventCode,
            severity,
            actor,
            nameof(SupportTicket),
            targetEntityId,
            json,
            _caller.SourceIp,
            _caller.CorrelationId,
            cancellationToken).ConfigureAwait(false);
    }
}
