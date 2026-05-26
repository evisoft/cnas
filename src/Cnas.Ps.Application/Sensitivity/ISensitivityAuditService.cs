using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Cnas.Ps.Application.Sensitivity;

/// <summary>
/// R0228 / TOR SEC 033 — audit facade invoked by the
/// <c>SensitivityHeaderMiddleware</c> when an API response carries any
/// <see cref="Cnas.Ps.Contracts.Security.SensitivityLabel.Restricted"/> field. The
/// facade writes a Sensitive (not Critical, to avoid log spam) audit row tagged with
/// the resource and the list of disclosed fields, and increments the
/// <c>cnas.sensitivity.restricted_access</c> counter for fleet-wide trending.
/// </summary>
/// <remarks>
/// <para>
/// <b>Severity choice.</b> Restricted access is by definition expected — citizens
/// read their own files, support staff investigate cases. Recording every disclosure
/// at <c>Critical</c> would drown the SOC; <c>Sensitive</c> matches the existing
/// "access to confidential data" rung per <c>AuditSeverity</c> documentation.
/// </para>
/// <para>
/// <b>One write per request.</b> The middleware deliberately collapses the
/// disclosure of N Restricted fields on a single response into a single audit row
/// — the row payload lists every field — so the audit timeline stays readable.
/// </para>
/// </remarks>
public interface ISensitivityAuditService
{
    /// <summary>
    /// Records that a response carrying <see cref="Cnas.Ps.Contracts.Security.SensitivityLabel.Restricted"/>
    /// fields was sent to the wire.
    /// </summary>
    /// <param name="resource">
    /// Logical resource name (typically the DTO type name, e.g. <c>InsuredPersonOutput</c>).
    /// Used as the audit row's <c>targetEntity</c> and as the <c>resource</c> counter
    /// tag — keep the cardinality bounded.
    /// </param>
    /// <param name="recordSqid">
    /// Sqid id of the specific record disclosed when known (<c>null</c> for list
    /// endpoints that span many records).
    /// </param>
    /// <param name="propertyNames">
    /// Names of every Restricted-labelled property included in the payload. Embedded in
    /// the audit row's JSON detail so investigators can replay disclosures field-by-field.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the audit row has been enqueued.</returns>
    Task RecordRestrictedAccessAsync(
        string resource,
        string? recordSqid,
        IReadOnlyCollection<string> propertyNames,
        CancellationToken ct);
}
