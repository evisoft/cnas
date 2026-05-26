using System.Collections.Generic;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Reporting;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.Reporting;

/// <summary>
/// R1906 / TOR Annex 6 — production implementation of
/// <see cref="IReportDistributionDispatcher"/>. Loads every active rule
/// matching the supplied report code, resolves recipients through
/// <see cref="IReportRecipientResolver"/>, selects the right channel
/// handler, and persists one <see cref="ReportDistributionDispatch"/> per
/// consulted rule.
/// </summary>
public sealed class ReportDistributionDispatcher : IReportDistributionDispatcher
{
    private readonly ICnasDbContext _db;
    private readonly ICnasTimeProvider _clock;
    private readonly ICallerContext _caller;
    private readonly IReportRecipientResolver _resolver;
    private readonly IReadOnlyDictionary<ReportDistributionChannel, IReportDistributionChannelHandler> _handlers;
    private readonly IValidator<ReportDispatchInputDto> _validator;

    /// <summary>Constructs the dispatcher with its scoped collaborators.</summary>
    /// <param name="db">Writer DB context.</param>
    /// <param name="clock">UTC clock abstraction.</param>
    /// <param name="caller">Caller context — used to attribute the dispatch rows.</param>
    /// <param name="resolver">Recipient resolver.</param>
    /// <param name="handlers">Per-channel handlers; the dispatcher selects by <see cref="IReportDistributionChannelHandler.Channel"/>.</param>
    /// <param name="validator">Input validator for the dispatch envelope.</param>
    public ReportDistributionDispatcher(
        ICnasDbContext db,
        ICnasTimeProvider clock,
        ICallerContext caller,
        IReportRecipientResolver resolver,
        IEnumerable<IReportDistributionChannelHandler> handlers,
        IValidator<ReportDispatchInputDto> validator)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(handlers);
        ArgumentNullException.ThrowIfNull(validator);
        _db = db;
        _clock = clock;
        _caller = caller;
        _resolver = resolver;
        _handlers = handlers.ToDictionary(h => h.Channel);
        _validator = validator;
    }

    /// <inheritdoc />
    public async Task<Result<ReportDistributionDispatchSummaryDto>> DispatchAsync(
        ReportDispatchInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var validation = await _validator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<ReportDistributionDispatchSummaryDto>.Failure(
                ErrorCodes.ValidationFailed, validation.ToString());
        }

        // Map the format string to its enum value — already validated above so the parse always succeeds.
        var requestedFormat = Enum.Parse<ReportDeliveryFormat>(input.Format, ignoreCase: false);

        var today = _clock.TodayUtc;
        var rules = await _db.ReportDistributionRules
            .Where(r => r.ReportCode == input.ReportCode
                && r.IsActive
                && r.EffectiveFrom <= today
                && (r.EffectiveUntil == null || r.EffectiveUntil >= today))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var delivered = 0;
        var failed = 0;
        var skipped = 0;
        var actor = _caller.UserSqid ?? "system";

        foreach (var rule in rules)
        {
            cancellationToken.ThrowIfCancellationRequested();

            CnasMeter.ReportDistributionDispatchAttempted.Add(1,
                new KeyValuePair<string, object?>("channel", rule.Channel.ToString()));

            // Format mismatch ⇒ Skipped without invoking the handler.
            if (rule.Format != requestedFormat)
            {
                await PersistDispatchAsync(rule, input, ReportDispatchStatus.Skipped, "FORMAT_MISMATCH", actor, cancellationToken).ConfigureAwait(false);
                skipped++;
                CnasMeter.ReportDistributionDispatchOutcome.Add(1,
                    new KeyValuePair<string, object?>("channel", rule.Channel.ToString()),
                    new KeyValuePair<string, object?>("status", nameof(ReportDispatchStatus.Skipped)));
                continue;
            }

            // No registered handler for the rule's channel — treat as Skipped.
            if (!_handlers.TryGetValue(rule.Channel, out var handler))
            {
                await PersistDispatchAsync(rule, input, ReportDispatchStatus.Skipped, "NO_HANDLER_REGISTERED", actor, cancellationToken).ConfigureAwait(false);
                skipped++;
                CnasMeter.ReportDistributionDispatchOutcome.Add(1,
                    new KeyValuePair<string, object?>("channel", rule.Channel.ToString()),
                    new KeyValuePair<string, object?>("status", nameof(ReportDispatchStatus.Skipped)));
                continue;
            }

            var resolved = await _resolver.ResolveAsync(rule, cancellationToken).ConfigureAwait(false);
            if (resolved.IsFailure)
            {
                await PersistDispatchAsync(rule, input, ReportDispatchStatus.Failed, "RECIPIENT_RESOLUTION_FAILED", actor, cancellationToken).ConfigureAwait(false);
                failed++;
                CnasMeter.ReportDistributionDispatchOutcome.Add(1,
                    new KeyValuePair<string, object?>("channel", rule.Channel.ToString()),
                    new KeyValuePair<string, object?>("status", nameof(ReportDispatchStatus.Failed)));
                continue;
            }

            var recipients = resolved.Value;
            if (recipients.Count == 0)
            {
                await PersistDispatchAsync(rule, input, ReportDispatchStatus.Skipped, "NO_RECIPIENTS_RESOLVED", actor, cancellationToken).ConfigureAwait(false);
                skipped++;
                CnasMeter.ReportDistributionDispatchOutcome.Add(1,
                    new KeyValuePair<string, object?>("channel", rule.Channel.ToString()),
                    new KeyValuePair<string, object?>("status", nameof(ReportDispatchStatus.Skipped)));
                continue;
            }

            // Aggregate the per-recipient outcomes into a single rule-level
            // outcome. If every recipient was delivered → Delivered. If at
            // least one failed → Failed. Otherwise → Skipped (e.g. all rows
            // skipped by the handler).
            var anyDelivered = false;
            var anyFailed = false;
            string? lastFailureReason = null;
            foreach (var recipient in recipients)
            {
                try
                {
                    var outcome = await handler.DispatchAsync(rule, input, recipient.Address, cancellationToken)
                        .ConfigureAwait(false);
                    switch (outcome.Status)
                    {
                        case ReportDispatchStatus.Delivered: anyDelivered = true; break;
                        case ReportDispatchStatus.Failed: anyFailed = true; lastFailureReason = outcome.FailureReason; break;
                        case ReportDispatchStatus.Skipped: lastFailureReason ??= outcome.FailureReason; break;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    anyFailed = true;
                    lastFailureReason = $"DISPATCH.{ex.GetType().Name}";
                }
            }

            ReportDispatchStatus finalStatus;
            string? finalReason;
            if (anyFailed) { finalStatus = ReportDispatchStatus.Failed; finalReason = lastFailureReason; }
            else if (anyDelivered) { finalStatus = ReportDispatchStatus.Delivered; finalReason = null; }
            else { finalStatus = ReportDispatchStatus.Skipped; finalReason = lastFailureReason; }

            await PersistDispatchAsync(rule, input, finalStatus, finalReason, actor, cancellationToken).ConfigureAwait(false);
            switch (finalStatus)
            {
                case ReportDispatchStatus.Delivered: delivered++; break;
                case ReportDispatchStatus.Failed: failed++; break;
                case ReportDispatchStatus.Skipped: skipped++; break;
            }
            CnasMeter.ReportDistributionDispatchOutcome.Add(1,
                new KeyValuePair<string, object?>("channel", rule.Channel.ToString()),
                new KeyValuePair<string, object?>("status", finalStatus.ToString()));
        }

        return Result<ReportDistributionDispatchSummaryDto>.Success(
            new ReportDistributionDispatchSummaryDto(
                TotalRules: rules.Count,
                Delivered: delivered,
                Failed: failed,
                Skipped: skipped));
    }

    /// <summary>
    /// Persists one <see cref="ReportDistributionDispatch"/> row capturing the terminal status.
    /// </summary>
    /// <param name="rule">The matched rule.</param>
    /// <param name="input">The dispatch envelope.</param>
    /// <param name="status">Terminal status.</param>
    /// <param name="failureReason">Sanitised reason for non-success outcomes; null on Delivered.</param>
    /// <param name="actor">Audit-attribution actor.</param>
    /// <param name="cancellationToken">Cancellation propagated from the caller.</param>
    /// <returns>A task representing the SaveChanges operation.</returns>
    private async Task PersistDispatchAsync(
        ReportDistributionRule rule,
        ReportDispatchInputDto input,
        ReportDispatchStatus status,
        string? failureReason,
        string actor,
        CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var row = new ReportDistributionDispatch
        {
            RuleId = rule.Id,
            ReportRunSqid = input.ReportRunSqid,
            Channel = rule.Channel,
            RecipientKind = rule.RecipientKind,
            RecipientCode = rule.RecipientCode,
            Status = status,
            DispatchedAt = now,
            DeliveredAt = status == ReportDispatchStatus.Delivered ? now : null,
            FailureReason = failureReason,
            RetryCount = 0,
            CreatedAtUtc = now,
            CreatedBy = actor,
            IsActive = true,
        };
        _db.ReportDistributionDispatches.Add(row);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
