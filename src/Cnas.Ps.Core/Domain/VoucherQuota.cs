namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R1000..R1034 / TOR §3.2-AB..AD — monthly + annual voucher allotment for the
/// quota-gated spa / rehabilitation / sanatorium passports (3.2-AB Bilet
/// tratament balneo veterani, 3.2-AC Bilet tratament Cernobîl, 3.2-AD Bilet
/// tratament balneo asigurați). One row binds a stable passport code to a
/// calendar year and tracks both the configured cap and the running usage.
/// </summary>
/// <remarks>
/// <para>
/// <b>Natural-key uniqueness.</b> <see cref="PassportCode"/> + <see cref="Year"/>
/// is unique. The EF configuration enforces this via a filtered partial index
/// scoped to <see cref="AuditableEntity.IsActive"/> = <c>true</c> so an
/// operator can deactivate a stale row and seed a new one without colliding.
/// </para>
/// <para>
/// <b>External id.</b> Implements <see cref="IExternalId"/> — operators
/// reference quota rows by Sqid through the admin surface, per CLAUDE.md
/// RULE 3.
/// </para>
/// <para>
/// <b>Atomicity contract.</b> <see cref="UsedThisMonth"/> /
/// <see cref="UsedThisYear"/> are mutated only by
/// <c>IVoucherQuotaService.ReserveAsync</c> / <c>ReleaseAsync</c>, which
/// guard against double-spend via EF's xmin concurrency token plus an
/// in-service availability re-check after acquiring the row.
/// </para>
/// </remarks>
public sealed class VoucherQuota : AuditableEntity, IExternalId
{
    /// <summary>
    /// Stable SCREAMING_SNAKE_CASE or §3.2-AB/AC/AD-style passport code this
    /// quota row applies to (e.g. <c>3.2-AB</c>, <c>3.2-AC</c>,
    /// <c>3.2-AD</c>). Length capped at 32 characters at the persistence
    /// layer.
    /// </summary>
    public required string PassportCode { get; set; }

    /// <summary>
    /// Calendar year (e.g. <c>2026</c>) the quota row applies to. Used by
    /// the service when resolving the current-year row at
    /// <c>CheckAvailabilityAsync</c> / <c>ReserveAsync</c> time.
    /// </summary>
    public int Year { get; set; }

    /// <summary>
    /// Operator-configured cap on the number of vouchers that may be issued
    /// against this passport in any single calendar month. <c>0</c> means
    /// "no monthly cap" (only the annual cap applies).
    /// </summary>
    public int MonthlyQuota { get; set; }

    /// <summary>
    /// Operator-configured cap on the number of vouchers that may be issued
    /// against this passport across the full calendar year. <c>0</c> means
    /// "no annual cap" (only the monthly cap applies).
    /// </summary>
    public int AnnualQuota { get; set; }

    /// <summary>
    /// Running count of vouchers reserved in the current calendar month.
    /// Reset to <c>0</c> by the per-month sweep (or implicitly when a new
    /// month's first <c>ReserveAsync</c> call rotates the snapshot via
    /// <see cref="UsedMonth"/>).
    /// </summary>
    public int UsedThisMonth { get; set; }

    /// <summary>
    /// Running count of vouchers reserved across the full calendar year.
    /// Reset only when the row's <see cref="Year"/> rolls over (a new row
    /// is created for the next year).
    /// </summary>
    public int UsedThisYear { get; set; }

    /// <summary>
    /// Month-of-year (1..12) the <see cref="UsedThisMonth"/> counter
    /// refers to. Lets the service detect a month rollover and reset the
    /// monthly counter on first reservation in a new month without
    /// requiring a separate sweep job.
    /// </summary>
    public int UsedMonth { get; set; }
}
