namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// R0321 / R0224 / UI 008 — tunable parameters for the application autosave / version
/// service. Bound from the <c>Cnas:ApplicationAutosave</c> configuration section so
/// operators can adjust the per-application autosave cap and the payload budget
/// without redeploying.
/// </summary>
/// <remarks>
/// <para>
/// <b>Default rationale.</b>
/// <list type="bullet">
///   <item>
///     <description><see cref="MaxAutosavesPerApplication"/> = 50 — a generous cap that
///     covers ~50 minutes of an autosave tick every minute (typical UI cadence) before
///     the oldest auto-save row is pruned. Manual saves, submits, and reverts do NOT
///     count toward the cap and are NEVER pruned.</description>
///   </item>
///   <item>
///     <description><see cref="MaxFormDataKb"/> = 500 — the wire-size ceiling enforced
///     by the FluentValidation rule on <c>ApplicationVersionSaveDto</c>. A larger payload
///     indicates a serialiser bug or abuse attempt; the autosave subsystem is not a
///     freeform storage area.</description>
///   </item>
/// </list>
/// </para>
/// </remarks>
public sealed class ApplicationAutosaveOptions
{
    /// <summary>Configuration section name used by the host bindings.</summary>
    public const string SectionName = "Cnas:ApplicationAutosave";

    /// <summary>
    /// Maximum number of <see cref="Cnas.Ps.Core.Domain.ApplicationVersionSource.Autosave"/>
    /// rows retained per <see cref="Cnas.Ps.Core.Domain.ServiceApplication"/>. When the
    /// 51st auto-save would push the count past this cap the oldest auto-save row is
    /// HARD-DELETED in the same transaction so the cap is enforced atomically. Manual
    /// saves, submits, and reverts are excluded from this cap and never pruned.
    /// Default 50.
    /// </summary>
    public int MaxAutosavesPerApplication { get; set; } = 50;

    /// <summary>
    /// Hard cap on the UTF-8 byte length of <c>FormDataJson</c>, expressed in kilobytes
    /// (1 KB = 1024 bytes). The validator returns
    /// <see cref="Cnas.Ps.Core.Common.ErrorCodes.ValidationFailed"/> on overrun.
    /// Default 500 (≈ 512 KB).
    /// </summary>
    public int MaxFormDataKb { get; set; } = 500;
}
