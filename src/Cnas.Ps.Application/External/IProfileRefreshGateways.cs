using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.External;

/// <summary>
/// R0363 — typed facade for the RSP (civil register) profile-refresh path. Returns the
/// deltas needed to bring our local contributor cache in line with the upstream RSP
/// snapshot. Implementations may proxy through <see cref="IMConnectClient"/> or invoke a
/// partner-direct fallback per R0104 — the contract is identical either way.
/// </summary>
/// <remarks>
/// <para>
/// <b>What lives here vs. <see cref="IRspClient"/>.</b> <see cref="IRspClient"/> returns
/// the raw RSP person snapshot; this facade returns the field-level deltas in the shape
/// the contributor-side writers consume. Keeping them separate means a future caller
/// that wants the raw snapshot for display (e.g. UI 052 "verify RSP data") does not
/// drag along the refresh-side delta computation.
/// </para>
/// <para>
/// <b>Deferred.</b> Real RSP SOAP calls require an NDA-gated WSDL from MEGA; the
/// MockRspGateway implementation in Infrastructure returns hand-coded sample deltas so
/// the rest of the pipeline can be developed independently. Real wiring lands once the
/// per-system contracts are obtained.
/// </para>
/// </remarks>
public interface IRspGateway
{
    /// <summary>
    /// Returns the set of profile-refresh deltas for the supplied IDNP. An empty list is
    /// a valid success outcome ("no upstream changes since the last sync").
    /// </summary>
    /// <param name="idnp">The contributor's IDNP (validated upstream).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Result<IReadOnlyList<ProfileRefreshDeltaDto>>> FetchDeltasAsync(string idnp, CancellationToken ct = default);
}

/// <summary>
/// R0363 — typed facade for the RSUD (legal-person register) profile-refresh path.
/// </summary>
/// <remarks>
/// Symmetric to <see cref="IRspGateway"/> but keyed by IDNO. Used for contributors that
/// represent a legal-person employer (the InsuredPerson row's
/// <c>ContributorActivityPeriod</c> child carries the employer IDNO that RSUD
/// authoritatively governs).
/// </remarks>
public interface IRsudGateway
{
    /// <summary>
    /// Returns the set of profile-refresh deltas for the contributor identified by
    /// <paramref name="idnp"/>; the implementation joins on the recorded employer IDNO
    /// internally and emits deltas for the contributor's activity-period rows.
    /// </summary>
    /// <param name="idnp">The contributor's IDNP.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Result<IReadOnlyList<ProfileRefreshDeltaDto>>> FetchDeltasAsync(string idnp, CancellationToken ct = default);
}

/// <summary>
/// R0363 — typed facade for the SI SFS (State Tax Service) profile-refresh path.
/// </summary>
/// <remarks>
/// SI SFS authoritatively governs salary declarations. The refresh path emits deltas
/// that re-base the contributor's voluntary social-insurance contract row (declared
/// monthly amount) when SFS has more recent data than CNAS.
/// </remarks>
public interface ISiSfsGateway
{
    /// <summary>
    /// Returns the set of profile-refresh deltas for the contributor identified by
    /// <paramref name="idnp"/>.
    /// </summary>
    /// <param name="idnp">The contributor's IDNP.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Result<IReadOnlyList<ProfileRefreshDeltaDto>>> FetchDeltasAsync(string idnp, CancellationToken ct = default);
}
