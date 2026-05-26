using System.Threading;

namespace Cnas.Ps.Infrastructure.Scheduling;

/// <summary>
/// R2173 / TOR PSR 004 — in-process singleton that holds the runtime
/// admin-override state for the peak-hour gate. Mirrors the boot-time
/// <see cref="PeakHourGateOptions.GlobalOverride"/> default and is mutated
/// by the admin controller (<c>POST /api/admin/peak-hour-gate/override</c>)
/// without round-tripping through the configuration provider.
/// </summary>
/// <remarks>
/// <para>
/// The bound <see cref="PeakHourGateOptions.GlobalOverride"/> only seeds the
/// initial value at process start. Operators flip the override through the
/// admin endpoint which calls <see cref="SetOverride(bool)"/>; the gate then
/// observes the new value on its next evaluation. This is intentionally
/// in-memory (per-process) — deferred per-tenant / persisted overrides are
/// out of scope for R2173 and would be cluster-coordinated via shared cache.
/// </para>
/// <para>
/// Reads and writes use <c>Volatile.Read</c> / <c>Volatile.Write</c> to
/// guarantee cross-CPU visibility without the cost of a lock; the underlying
/// field is an <see cref="int"/> rather than a <see cref="bool"/> because
/// <see cref="Volatile"/> does not directly support <see cref="bool"/>.
/// </para>
/// </remarks>
public sealed class PeakHourGateOverrideStore
{
    private int _override; // 0 = false, 1 = true

    /// <summary>
    /// Constructs the store seeded from the bound
    /// <see cref="PeakHourGateOptions.GlobalOverride"/> default. Subsequent
    /// option-config refreshes do NOT mutate the store — once the process
    /// is running the admin endpoint is the only authoritative writer.
    /// </summary>
    /// <param name="initialOverride">Initial override state at process start.</param>
    public PeakHourGateOverrideStore(bool initialOverride)
    {
        _override = initialOverride ? 1 : 0;
    }

    /// <summary>Reads the current override state.</summary>
    /// <returns><c>true</c> when the global override is active.</returns>
    public bool IsOverrideActive() => Volatile.Read(ref _override) != 0;

    /// <summary>Sets the override state to the supplied value.</summary>
    /// <param name="value">Target state.</param>
    public void SetOverride(bool value) => Volatile.Write(ref _override, value ? 1 : 0);
}
