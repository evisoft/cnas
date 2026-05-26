using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.UseCases;

/// <summary>
/// R1000..R1034 / TOR §3.2-AB..AD — operator-facing voucher-quota engine
/// that gates the spa / rehabilitation / sanatorium passports (3.2-AB Bilet
/// tratament balneo veterani, 3.2-AC Bilet tratament Cernobîl, 3.2-AD Bilet
/// tratament balneo asigurați). Hosts the per-passport monthly + annual cap
/// configuration, the availability lookup, and the reserve / release
/// primitives used by the application-processing pipeline at decision time.
/// </summary>
/// <remarks>
/// <para>
/// <b>Atomicity contract.</b> <see cref="ReserveAsync"/> increments the
/// monthly + annual counters in a single <c>SaveChangesAsync</c> call so
/// EF's xmin concurrency token blocks a double-spend race. A re-check
/// against the cap happens AFTER the row is materialised so a concurrent
/// reservation cannot slip past the quota.
/// </para>
/// <para>
/// <b>Month-rollover semantics.</b> The first reservation in a new
/// calendar month implicitly resets <c>UsedThisMonth</c> to <c>0</c> and
/// updates <c>UsedMonth</c> to the new month — no separate sweep job is
/// required.
/// </para>
/// </remarks>
public interface IVoucherQuotaService
{
    /// <summary>Stable failure code: the quota row for the requested (passport, year) does not exist.</summary>
    public const string QuotaNotConfiguredCode = "VOUCHER.QUOTA_NOT_CONFIGURED";

    /// <summary>Stable failure code: no slots remain for the requested month / year.</summary>
    public const string QuotaExhaustedCode = "VOUCHER.QUOTA_EXHAUSTED";

    /// <summary>Stable failure code: a release request was issued against a zero counter.</summary>
    public const string QuotaUnderflowCode = "VOUCHER.QUOTA_UNDERFLOW";

    /// <summary>Stable audit event code emitted when a quota is configured (created or updated).</summary>
    public const string AuditConfigured = "VOUCHER.QUOTA_CONFIGURED";

    /// <summary>Stable audit event code emitted when a slot is reserved.</summary>
    public const string AuditReserved = "VOUCHER.QUOTA_RESERVED";

    /// <summary>Stable audit event code emitted when a slot is released back to the pool.</summary>
    public const string AuditReleased = "VOUCHER.QUOTA_RELEASED";

    /// <summary>
    /// Reads the current availability snapshot for the given
    /// (passport, year, month) tuple WITHOUT mutating the row.
    /// </summary>
    /// <param name="passportCode">Stable passport code (e.g. <c>3.2-AB</c>).</param>
    /// <param name="year">Calendar year.</param>
    /// <param name="month">Month-of-year (1..12).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result{T}.Success(T)"/> with the availability snapshot;
    /// <see cref="QuotaNotConfiguredCode"/> when no quota row exists.
    /// </returns>
    Task<Result<VoucherQuotaCheckDto>> CheckAvailabilityAsync(
        string passportCode,
        int year,
        int month,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically increments <c>UsedThisMonth</c> + <c>UsedThisYear</c> for
    /// the given (passport, year, month) tuple. Fails fast when no slot
    /// remains under either cap.
    /// </summary>
    /// <param name="passportCode">Stable passport code.</param>
    /// <param name="year">Calendar year.</param>
    /// <param name="month">Month-of-year (1..12).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result.Success()"/> when the reservation succeeded;
    /// <see cref="QuotaNotConfiguredCode"/> when no quota row exists;
    /// <see cref="QuotaExhaustedCode"/> when both caps refuse the slot.
    /// </returns>
    Task<Result> ReserveAsync(
        string passportCode,
        int year,
        int month,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Decrements the monthly + annual counters by 1 — used when an
    /// already-reserved voucher is cancelled and the slot returned to the
    /// pool.
    /// </summary>
    /// <param name="passportCode">Stable passport code.</param>
    /// <param name="year">Calendar year of the original reservation.</param>
    /// <param name="month">Month-of-year (1..12) of the original reservation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result.Success()"/> when the release succeeded;
    /// <see cref="QuotaNotConfiguredCode"/> when no quota row exists;
    /// <see cref="QuotaUnderflowCode"/> when the counter would go negative.
    /// </returns>
    Task<Result> ReleaseAsync(
        string passportCode,
        int year,
        int month,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Operator-facing seed / upsert primitive. Creates a quota row when
    /// none exists for the (passport, year) tuple; otherwise updates the
    /// existing caps. Idempotent.
    /// </summary>
    /// <param name="passportCode">Stable passport code.</param>
    /// <param name="year">Calendar year.</param>
    /// <param name="monthlyQuota">Monthly cap (≥ 0; 0 disables the monthly cap).</param>
    /// <param name="annualQuota">Annual cap (≥ 0; 0 disables the annual cap).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The persisted DTO on success.</returns>
    Task<Result<VoucherQuotaDto>> ConfigureQuotaAsync(
        string passportCode,
        int year,
        int monthlyQuota,
        int annualQuota,
        CancellationToken cancellationToken = default);
}
