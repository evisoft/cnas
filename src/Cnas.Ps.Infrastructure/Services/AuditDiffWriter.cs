using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Audit;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// Default <see cref="IAuditDiffWriter"/> implementation that resolves the
/// per-entity <see cref="AuditFieldPolicy"/> via <see cref="IAuditFieldPolicyResolver"/>,
/// computes a structured diff via <see cref="IAuditDiffComputer"/>, and writes the
/// audit row via <see cref="IAuditService"/> with the diff JSON attached to
/// <c>DetailsJson</c> AFTER <see cref="PiiRedactor.Redact(string?)"/> normalisation.
/// </summary>
/// <remarks>
/// <para>
/// <b>No-policy fall-through.</b> When the resolver returns <c>null</c> the writer
/// behaves identically to a direct
/// <see cref="IAuditService.RecordAsync(string, AuditSeverity, string, string?, long?, string, string?, string?, CancellationToken)"/>
/// call — the audit row is still written with a minimal details payload. This is
/// the no-behavioural-break invariant.
/// </para>
/// <para>
/// <b>PiiRedactor wraps the diff JSON.</b> The diff payload is fed through
/// <see cref="PiiRedactor.Redact(string?)"/> before being handed to the audit
/// service so the R0194 hash chain reflects the on-disk shape exactly. The
/// computer already replaces suppressed-field values with <c>"[redacted]"</c>;
/// the redactor adds the default PII key list on top.
/// </para>
/// </remarks>
public sealed class AuditDiffWriter : IAuditDiffWriter
{
    private readonly IAuditFieldPolicyResolver _resolver;
    private readonly IAuditDiffComputer _computer;
    private readonly IAuditService _audit;
    private readonly ICallerContext _caller;

    /// <summary>Constructs the writer with its DI collaborators.</summary>
    /// <param name="resolver">Resolves the per-entity policy view.</param>
    /// <param name="computer">Computes the structured before/after diff.</param>
    /// <param name="audit">Underlying audit-write facade.</param>
    /// <param name="caller">Provides the actor / source-IP / correlation-id stamps.</param>
    public AuditDiffWriter(
        IAuditFieldPolicyResolver resolver,
        IAuditDiffComputer computer,
        IAuditService audit,
        ICallerContext caller)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(computer);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(caller);
        _resolver = resolver;
        _computer = computer;
        _audit = audit;
        _caller = caller;
    }

    /// <inheritdoc />
    public async Task<Result> WriteIfDiffAsync<TEntity>(
        string eventCode,
        long entityId,
        TEntity? before,
        TEntity? after,
        CancellationToken cancellationToken = default)
        where TEntity : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventCode);
        if (before is null && after is null)
        {
            return Result.Failure(ErrorCodes.ValidationFailed, "Both before and after snapshots are null.");
        }

        var entityType = typeof(TEntity).Name;
        var actor = _caller.UserSqid ?? "system";

        var policy = _resolver.Resolve(entityType);
        if (policy is null)
        {
            // No-policy fall-through — still write the audit row with a minimal
            // payload so consumers see "the save happened" without diff context.
            var fallbackDetails = JsonSerializer.Serialize(new
            {
                entityType,
                entityId,
                hasBefore = before is not null,
                hasAfter = after is not null,
            });
            return await _audit.RecordAsync(
                eventCode: eventCode,
                severity: AuditSeverity.Notice,
                actorId: actor,
                targetEntity: entityType,
                targetEntityId: entityId,
                detailsJson: fallbackDetails,
                sourceIp: _caller.SourceIp,
                correlationId: _caller.CorrelationId,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        var diff = _computer.Compute(entityType, before, after, policy);
        if (diff is null)
        {
            // Policy says RequireAnyChange=true and nothing changed — skip the row.
            return Result.Success();
        }

        // Project the diff to a stable JSON shape — properties in a predictable
        // order so downstream consumers (audit explorer, SIEM forwarder) can chart
        // diff layouts over time.
        var diffPayload = JsonSerializer.Serialize(new
        {
            entityType = diff.EntityType,
            entityId = diff.EntityId,
            changes = diff.Entries.Select(e => new
            {
                property = e.PropertyName,
                before = e.BeforeJson,
                after = e.AfterJson,
            }).ToArray(),
        });

        // PiiRedactor application — keeps the R0194 hash chain consistent with the
        // on-disk shape. The computer has already redacted suppressed-field values;
        // the redactor adds the default PII key list (idnp / phone / email / etc.).
        var redactedDetails = PiiRedactor.Redact(diffPayload);

        return await _audit.RecordAsync(
            eventCode: eventCode,
            severity: policy.Severity,
            actorId: actor,
            targetEntity: entityType,
            targetEntityId: entityId,
            detailsJson: redactedDetails,
            sourceIp: _caller.SourceIp,
            correlationId: _caller.CorrelationId,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
