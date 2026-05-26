namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R2501 / TOR PIR 024 — stable record of when CNAS business hours run. Other
/// service-management aggregates reference this policy to compute "business
/// day" notice periods (rather than hard-coding "Saturday/Sunday" or the RM
/// holiday calendar inline). The default seed row (<c>RM_DEFAULT</c>) carries
/// 08:00–18:00 Mon–Fri in the <c>Europe/Chisinau</c> timezone and the active
/// RM legal-holiday list for the running year.
/// </summary>
/// <remarks>
/// <para>
/// <b>Natural-key uniqueness.</b> <see cref="Code"/> is the stable
/// SCREAMING_SNAKE_CASE identifier; the EF configuration enforces a unique
/// constraint. <see cref="MaintenanceWindow"/> references the policy by id
/// (via a FK) so the active row in the policy table can be swapped without
/// rewriting the window history.
/// </para>
/// <para>
/// <b>Holiday handling.</b> <see cref="HolidayDatesJson"/> is a JSON array of
/// <c>YYYY-MM-DD</c> strings. The string lives on the row so operators can
/// override the calendar without a code release. The service layer treats
/// any date in the list as a non-business day regardless of weekday.
/// </para>
/// </remarks>
public sealed class BusinessHoursPolicy : AuditableEntity, IExternalId
{
    /// <summary>
    /// Stable SCREAMING_SNAKE_CASE policy code (e.g. <c>RM_DEFAULT</c>,
    /// <c>RM_HOLIDAY_OVERRIDE_2026</c>). Pattern <c>^[A-Z][A-Z0-9_]{1,63}$</c>,
    /// length ≤ 64. Unique within the system.
    /// </summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Human-readable display name. Bounded to 256 characters.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Optional free-form description. Bounded to 1000 characters.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// Local-time opening hour (in <see cref="TimezoneId"/>). Default 08:00.
    /// </summary>
    public TimeOnly OpenTimeLocal { get; set; } = new(8, 0);

    /// <summary>
    /// Local-time closing hour (in <see cref="TimezoneId"/>). Default 18:00.
    /// </summary>
    public TimeOnly CloseTimeLocal { get; set; } = new(18, 0);

    /// <summary>
    /// Bitmask of business days where Mon=1, Tue=2, Wed=4, Thu=8, Fri=16,
    /// Sat=32, Sun=64. Default 31 (Mon–Fri).
    /// </summary>
    public int BusinessDaysMask { get; set; } = 0b0011111;

    /// <summary>
    /// IANA timezone id (e.g. <c>Europe/Chisinau</c>). Bounded to 64 chars;
    /// default <c>Europe/Chisinau</c>.
    /// </summary>
    public string TimezoneId { get; set; } = "Europe/Chisinau";

    /// <summary>
    /// JSON array of <c>YYYY-MM-DD</c> strings naming legal-holiday dates
    /// (any date in the list is a non-business day regardless of weekday).
    /// May be <c>null</c> for "no holidays configured".
    /// </summary>
    public string? HolidayDatesJson { get; set; }

    /// <summary>Internal id of the operator who registered the policy.</summary>
    public long RegisteredByUserId { get; set; }
}
