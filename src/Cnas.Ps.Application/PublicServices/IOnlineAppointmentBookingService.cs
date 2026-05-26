using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.PublicServices;

/// <summary>
/// R0512 / TOR CF 02.01 — anonymous online-appointment booking façade. CNAS
/// does NOT host the scheduling flow; instead the public surface exposes
/// (a) a directory of regional branches and (b) a resolver that substitutes a
/// selected branch into the configured external-scheduler deep-link template.
/// Each resolve call writes an audit row so analytics can correlate
/// click-through rates across branches without touching the external system.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why two endpoints.</b> The directory call is a pure read (cached
/// server-side) and surfaces the full active-branch list. The resolve call is
/// a side-effecting "log the click + emit the URL" operation per branch — it
/// must NOT be merged into the directory call because audit rows would
/// otherwise fire on every directory render, drowning analytics.
/// </para>
/// <para>
/// <b>Anonymous surface.</b> Both endpoints are unauthenticated. Neither path
/// requires CAPTCHA — the directory is a static read (no abuse surface beyond
/// rate-limit) and the resolver returns a deep-link URL only, not citizen
/// data.
/// </para>
/// </remarks>
public interface IOnlineAppointmentBookingService
{
    /// <summary>
    /// Returns the active branch directory plus the system-wide deep-link
    /// template. Ordering is stable alphabetical-by-name so consumers can
    /// cache the response client-side without sort-order drift.
    /// </summary>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// A <see cref="AppointmentBookingDirectoryDto"/> wrapping the list +
    /// template. The result is always Success — an empty branch list is
    /// surfaced as an empty <see cref="AppointmentBookingDirectoryDto.Branches"/>
    /// rather than a failure, because "no branches configured" is a deployment
    /// concern (operators back-fill) rather than a runtime error.
    /// </returns>
    Task<Result<AppointmentBookingDirectoryDto>> GetDirectoryAsync(CancellationToken ct = default);

    /// <summary>
    /// Resolves the deep-link URL for a selected branch, substituting
    /// <c>{branchCode}</c> into the configured template. Writes a
    /// <c>PUBLIC.APPOINTMENT_DEEPLINK</c> audit row carrying the branch code
    /// so analytics can chart click-through volume.
    /// </summary>
    /// <param name="branchCode">Stable branch code chosen by the citizen.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// On success a <see cref="AppointmentDeepLinkDto"/> with the rendered
    /// URL. On unknown / inactive branch a <see cref="ErrorCodes.NotFound"/>
    /// failure (stable code <c>"BRANCH_NOT_FOUND"</c> in the human message)
    /// so the controller can surface HTTP 404.
    /// </returns>
    Task<Result<AppointmentDeepLinkDto>> ResolveDeepLinkAsync(
        string branchCode,
        CancellationToken ct = default);
}
