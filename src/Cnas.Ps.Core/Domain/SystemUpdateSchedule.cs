namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R2503 / TOR PIR 022-023 — operator-configurable system-update schedule
/// registry row. Each row binds a stable schedule code to a cadence kind +
/// notice lead-time. Concrete update events
/// (<see cref="SystemUpdateEvent"/>) reference a schedule and inherit its
/// lead-time requirement.
/// </summary>
/// <remarks>
/// <para>
/// <b>Natural-key uniqueness.</b> <see cref="ScheduleCode"/> is the stable
/// SCREAMING_SNAKE_CASE identifier; the EF configuration enforces a unique
/// constraint.
/// </para>
/// <para>
/// <b>Notice lead-time defaults</b> (R2504):
/// Monthly / Quarterly / Annual = 30 days,
/// MajorVersion = 180 days (6 months),
/// Critical / Security = 0 days.
/// </para>
/// </remarks>
public sealed class SystemUpdateSchedule : AuditableEntity, IExternalId
{
    /// <summary>
    /// Stable SCREAMING_SNAKE_CASE schedule code (e.g.
    /// <c>MONTHLY_PATCH</c>, <c>MAJOR_VERSION_BUMP</c>). Pattern
    /// <c>^[A-Z][A-Z0-9_.]{1,63}$</c>, length ≤ 64. Unique within the system.
    /// </summary>
    public string ScheduleCode { get; set; } = string.Empty;

    /// <summary>Human-readable display name (3..256 chars).</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Cadence classification — see <see cref="UpdateCadenceKind"/>.</summary>
    public UpdateCadenceKind Cadence { get; set; }

    /// <summary>
    /// Calendar days of advance notice required before a concrete event under
    /// this schedule can be deployed. R2504 cadence defaults documented on
    /// the enum.
    /// </summary>
    public int NoticeLeadTimeDays { get; set; }

    /// <summary>Optional free-form description. Bounded to 2000 chars.</summary>
    public string? Description { get; set; }

    /// <summary>Internal id of the operator who registered the schedule.</summary>
    public long RegisteredByUserId { get; set; }
}
