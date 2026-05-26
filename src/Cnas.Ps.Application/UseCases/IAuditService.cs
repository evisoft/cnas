using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Application.UseCases;

/// <summary>UC23 — Journal events. Centralised audit logging façade (SEC 038-048).</summary>
public interface IAuditService
{
    /// <summary>Records an audit event. Critical events are mirrored to MLog per SEC 056.</summary>
    Task<Result> RecordAsync(
        string eventCode,
        AuditSeverity severity,
        string actorId,
        string? targetEntity,
        long? targetEntityId,
        string detailsJson,
        string? sourceIp,
        string? correlationId,
        CancellationToken cancellationToken = default);
}
