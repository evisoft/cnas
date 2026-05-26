namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R1403 / TOR §3.6-D — one qualifying career-achievement row attached to an
/// <see cref="AthletePensionAward"/>. Multiple rows may exist per award
/// (e.g. an athlete with both an Olympic gold and a world record contributes
/// two rows; a coach contributes one or more medal rows for trained athletes
/// plus a <see cref="AthleteAchievementKind.CoachYearsService"/> row).
/// </summary>
/// <remarks>
/// <para>
/// <b>Verification gate.</b> Only rows with <see cref="Verified"/> = true
/// contribute to the eligibility verdict and the amount computation. The
/// service layer flips <see cref="Verified"/> when an operator validates the
/// evidence (e.g. archival certificate, federation confirmation) — see
/// <c>IAthletePensionAwardService.VerifyCareerRecordAsync</c>.
/// </para>
/// <para>
/// <b>External id.</b> Implements <see cref="IExternalId"/> because the
/// outbound DTO carries a Sqid-encoded surrogate per CLAUDE.md RULE 3.
/// </para>
/// </remarks>
public sealed class AthleteCareerRecord : AuditableEntity, IExternalId
{
    /// <summary>FK to the parent <see cref="AthletePensionAward"/>.</summary>
    public long AwardId { get; set; }

    /// <summary>Kind of qualifying achievement (medal tier, record, or coach years-of-service sentinel).</summary>
    public AthleteAchievementKind AchievementKind { get; set; }

    /// <summary>Calendar year in which the achievement occurred (1900..3000 enforced at validator).</summary>
    public int AchievementYear { get; set; }

    /// <summary>
    /// Event name / discipline detail (3..256 chars). Examples: <c>Tokyo 2020 — 100m sprint</c>,
    /// <c>European Championships 2018 — 81kg</c>.
    /// </summary>
    public required string Event { get; set; }

    /// <summary>
    /// Populated only when <see cref="AchievementKind"/> is
    /// <see cref="AthleteAchievementKind.CoachYearsService"/> — the coach's
    /// total years of professional service. 1..80 enforced at validator.
    /// </summary>
    public int? Years { get; set; }

    /// <summary>True once an operator has verified the supporting evidence.</summary>
    public bool Verified { get; set; }

    /// <summary>UTC timestamp the row was verified; <c>null</c> until verified.</summary>
    public DateTime? VerifiedAt { get; set; }

    /// <summary>FK to the <see cref="UserProfile"/> who verified the row; <c>null</c> until verified.</summary>
    public int? VerifiedByUserId { get; set; }

    /// <summary>Operator-supplied verification note (3..1000 chars; null until verified).</summary>
    public string? VerificationNote { get; set; }

    /// <summary>
    /// Opaque reference to the supporting evidence document (Sqid, URL, or
    /// archive identifier). Capped at 256 chars at the persistence layer.
    /// </summary>
    public string? EvidenceDocumentReference { get; set; }
}
