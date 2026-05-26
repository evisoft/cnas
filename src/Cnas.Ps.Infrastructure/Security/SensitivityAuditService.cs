using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Sensitivity;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;

namespace Cnas.Ps.Infrastructure.Security;

/// <summary>
/// R0228 / TOR SEC 033 — concrete <see cref="ISensitivityAuditService"/> wrapping
/// <see cref="IAuditService"/>. Writes a Sensitive
/// <c>SENSITIVITY.RESTRICTED_ACCESS</c> row whose <c>detailsJson</c> lists the
/// disclosed resource, optional record Sqid, and every Restricted field name; also
/// increments <see cref="CnasMeter.SensitivityRestrictedAccess"/> tagged with the
/// resource.
/// </summary>
public sealed class SensitivityAuditService : ISensitivityAuditService
{
    /// <summary>Stable canonical event code for the Restricted-access audit row.</summary>
    public const string EventCode = "SENSITIVITY.RESTRICTED_ACCESS";

    /// <summary>System actor used when the call originates from the middleware rather than a user-driven service.</summary>
    private const string SystemActor = "system:sensitivity-header";

    private readonly IAuditService _audit;

    /// <summary>
    /// Creates the service. The DI container resolves the underlying audit pipeline.
    /// </summary>
    /// <param name="audit">Underlying audit service that performs the write.</param>
    public SensitivityAuditService(IAuditService audit)
    {
        ArgumentNullException.ThrowIfNull(audit);
        _audit = audit;
    }

    /// <inheritdoc />
    public async Task RecordRestrictedAccessAsync(
        string resource,
        string? recordSqid,
        IReadOnlyCollection<string> propertyNames,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(resource);
        ArgumentNullException.ThrowIfNull(propertyNames);

        // Tagged once per call; bounded cardinality on the resource name.
        CnasMeter.SensitivityRestrictedAccess.Add(1, new KeyValuePair<string, object?>("resource", resource));

        // Detail payload — JSON for SIEM ingest and human-readable forensics. We embed
        // the field names so an investigator can replay disclosures down to the field
        // without re-running the original request.
        var detailsJson = JsonSerializer.Serialize(new
        {
            resource,
            recordSqid,
            fields = propertyNames,
        });

        await _audit.RecordAsync(
            eventCode: EventCode,
            severity: AuditSeverity.Sensitive,
            actorId: SystemActor,
            targetEntity: resource,
            targetEntityId: null,
            detailsJson: detailsJson,
            sourceIp: null,
            correlationId: null,
            cancellationToken: ct).ConfigureAwait(false);
    }
}
