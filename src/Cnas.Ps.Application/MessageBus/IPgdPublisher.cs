using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.MessageBus;

/// <summary>
/// R0117 / CF 14.11 / TOR §2.5.5 — outbound publisher for Portalul guvernamental de date
/// (PGD), the Moldovan government open-data portal. CNAS publishes public-interest
/// datasets (e.g. anonymised statistical aggregates) so citizens and downstream systems
/// can consume them without scraping CNAS-specific UIs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Distinct from <c>IMCabinetPublisher</c>.</b> MCabinet is the per-citizen dashboard;
/// PGD is the public open-data surface. The two publishers share HTTP plumbing patterns
/// internally but live on separate contracts so per-target configuration (base URL,
/// rate limits, alerting) can diverge. Operators can disable one without affecting the
/// other.
/// </para>
/// <para>
/// <b>Push-only.</b> The publisher accepts a single dataset payload per call and
/// returns the upstream outcome. Re-publishing the same <c>DatasetCode</c> replaces the
/// prior revision on the PGD side (idempotent by upstream contract).
/// </para>
/// <para>
/// <b>Configuration-gated.</b> When the publisher is not configured for the current
/// environment, the call deterministically returns a <see cref="Result{T}"/> failure
/// with code <see cref="ErrorCodes.PgdNotConfigured"/> rather than throwing — the dossier
/// state machine treats this as advisory and never rolls back the local transaction
/// when the open-data mirror failed.
/// </para>
/// </remarks>
public interface IPgdPublisher
{
    /// <summary>
    /// Publishes the supplied dataset to PGD. Idempotent on
    /// <see cref="PgdDatasetPublishInputDto.DatasetCode"/>: re-publishing replaces the
    /// prior revision rather than producing a duplicate row on the portal.
    /// </summary>
    /// <param name="input">The dataset payload + metadata.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>
    /// <see cref="Result{T}.Success"/> with <see cref="PgdPublishOutcomeDto"/> describing
    /// the upstream outcome (Accepted / Rejected / Skipped);
    /// <see cref="ErrorCodes.PgdNotConfigured"/> when the publisher cannot even attempt
    /// the upstream call because the base URL is blank;
    /// <see cref="ErrorCodes.PgdPublishFailed"/> on transport / non-2xx upstream
    /// failure.
    /// </returns>
    System.Threading.Tasks.Task<Result<PgdPublishOutcomeDto>> PublishAsync(
        PgdDatasetPublishInputDto input,
        System.Threading.CancellationToken cancellationToken = default);
}
