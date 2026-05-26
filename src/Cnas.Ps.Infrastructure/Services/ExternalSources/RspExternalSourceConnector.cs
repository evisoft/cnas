using Cnas.Ps.Application.ExternalSources;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.Services.ExternalSources;

/// <summary>
/// R0203 / TOR CF 20.06 — placeholder RSP (Registrul de Stat al Populației)
/// implementation of <see cref="IExternalSourceConnector"/>. Returns a
/// deterministic <c>EXT_SRC.RSP_NOT_CONFIGURED</c> failure when no base URL
/// is configured. Production iterations replace the body with a real
/// MConnect SOAP fetch.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a placeholder.</b> RSP is consumed through MConnect, which is in
/// turn gated on an MEGA-issued service certificate plus an MConnect
/// agreement. Shipping a deterministic failure here lets operators wire the
/// configuration without leaving the system open to surprise dependencies
/// on day one. Once the certificate + agreement land, swap the body for the
/// real SOAP call.
/// </para>
/// <para>
/// <b>No throw.</b> The discipline rules require a well-typed
/// <c>Result.Failure</c>, never a thrown exception — operators see the
/// failure on the audit log instead of paging on the dead-letter queue.
/// </para>
/// </remarks>
public sealed class RspExternalSourceConnector : IExternalSourceConnector
{
    /// <summary>Stable failure code surfaced when RSP base URL is not configured.</summary>
    public const string NotConfiguredCode = "EXT_SRC.RSP_NOT_CONFIGURED";

    /// <summary>Stable upper-case source-system code matched by the ingestion service.</summary>
    public const string SourceCodeLiteral = "RSP";

    private readonly IOptions<ExternalSourceOptions> _options;

    /// <summary>Constructs the placeholder connector.</summary>
    /// <param name="options">Bound <see cref="ExternalSourceOptions"/> envelope.</param>
    public RspExternalSourceConnector(IOptions<ExternalSourceOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <inheritdoc />
    public string SourceCode => SourceCodeLiteral;

    /// <inheritdoc />
    public Task<Result<ExternalSourceFetchOutcomeDto>> FetchAsync(
        DateOnly asOfDate,
        CancellationToken cancellationToken = default)
    {
        // Refuse loudly when the RSP base URL is blank — production deployments
        // that forget to wire the MConnect endpoint must surface the
        // misconfiguration on the audit log + admin run row rather than
        // silently no-opping.
        var baseUrl = _options.Value.Rsp.BaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return Task.FromResult(Result<ExternalSourceFetchOutcomeDto>.Failure(
                NotConfiguredCode,
                "Cnas:ExternalSources:Rsp:BaseUrl is not configured."));
        }

        // Even when configured, this iteration declines to fetch — the real
        // MConnect SOAP wiring lands in a follow-up iteration. Keeping the
        // failure explicit makes the seam visible during code review and
        // operations dashboards never silently fail-open.
        return Task.FromResult(Result<ExternalSourceFetchOutcomeDto>.Failure(
            NotConfiguredCode,
            "RSP MConnect connector is not wired yet (placeholder)."));
    }
}
