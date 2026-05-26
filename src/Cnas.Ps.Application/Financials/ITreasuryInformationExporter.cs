using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Financials;

/// <summary>
/// R0816 / TOR BP 1.2-G — service façade that aggregates the data the State
/// Treasury needs for one operating cycle into a machine-readable payload
/// (XML or CSV). Combines:
/// <list type="bullet">
///   <item>Approved / IssuedToTreasury <see cref="Cnas.Ps.Core.Domain.BassRefund"/>
///     rows whose dispatch has not been recorded yet (Treasury still owes the
///     payer the money).</item>
///   <item>Open / PartiallyPaid <see cref="Cnas.Ps.Core.Domain.Claim"/> rows
///     opened within the past 30 days (Treasury is expected to wire the
///     contributions to BASS).</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// <b>Format negotiation.</b> The caller picks XML or CSV via the
/// <c>format</c> route parameter; the service emits the matching encoding
/// with a stable filename of the form
/// <c>treasury-info-{yyyy-MM-dd}.{xml|csv}</c>.
/// </para>
/// <para>
/// <b>Deferred work.</b> The real Treasury submission format (e.g. ISO 20022
/// pain.001) is not part of this iteration — today we emit a simple internal
/// schema that operators can transform downstream. A future iteration will
/// implement the regulator-mandated XML schema.
/// </para>
/// </remarks>
public interface ITreasuryInformationExporter
{
    /// <summary>
    /// R0816 — builds the export payload for the supplied calendar date.
    /// </summary>
    /// <param name="forDate">Operating date the export targets; must be ≤ today.</param>
    /// <param name="format">Stable format tag — <c>XML</c> or <c>CSV</c> (case-insensitive).</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// On success the populated <see cref="TreasuryInformationExportDto"/>;
    /// on validation failure <see cref="ErrorCodes.ValidationFailed"/>.
    /// </returns>
    Task<Result<TreasuryInformationExportDto>> GenerateAsync(
        DateOnly forDate,
        string format,
        CancellationToken ct = default);
}
