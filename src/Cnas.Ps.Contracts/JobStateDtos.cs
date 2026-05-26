namespace Cnas.Ps.Contracts;

/// <summary>
/// R0204 / TOR CF 20.07-08 — externally-visible projection of one scheduled
/// Quartz job + its current state. Surfaced by
/// <c>Cnas.Ps.Application.UseCases.IJobStateInspector</c> and rendered on the
/// admin "Jobs dashboard" page so technical administrators can see at a glance
/// which background jobs are registered, when they last fired, when they next
/// fire, and whether they are currently paused or running.
/// </summary>
/// <remarks>
/// <para>
/// <b>Identifiers.</b> <see cref="JobName"/> is the Quartz <c>JobKey.Name</c>
/// (e.g. <c>mpay-dispatcher</c>) — like the <see cref="AutomationRunNowRequest"/>
/// surface it is NOT a Sqid because the job key is itself the public name of
/// the job and operators reference it directly in runbooks. CLAUDE.md RULE 3
/// does NOT apply here.
/// </para>
/// <para>
/// <b>Time fields.</b> Every timestamp is UTC per CLAUDE.md cross-cutting.
/// <see cref="NextFireUtc"/> is <c>null</c> when the trigger has no future
/// fire (e.g. a one-shot replay that has already fired, or a paused trigger
/// with an exhausted schedule). <see cref="LastFireUtc"/> is <c>null</c> when
/// the trigger has never fired in the lifetime of this scheduler instance.
/// </para>
/// </remarks>
/// <param name="JobName">Quartz job name (e.g. <c>mpay-dispatcher</c>).</param>
/// <param name="JobGroup">Quartz job group — usually <c>"DEFAULT"</c>.</param>
/// <param name="TriggerName">Quartz trigger name attached to the job.</param>
/// <param name="NextFireUtc">UTC instant of the next scheduled fire; <c>null</c> when the trigger is exhausted.</param>
/// <param name="LastFireUtc">UTC instant of the most recent fire; <c>null</c> when the trigger has never fired.</param>
/// <param name="State">
/// One of <c>"Normal"</c>, <c>"Paused"</c>, <c>"Complete"</c>, <c>"Blocked"</c>, <c>"Error"</c>,
/// or <c>"None"</c> — verbatim Quartz <c>TriggerState</c> name. Operators reading the dashboard
/// recognise these strings from the Quartz documentation.
/// </param>
public sealed record JobStateDto(
    string JobName,
    string JobGroup,
    string TriggerName,
    DateTime? NextFireUtc,
    DateTime? LastFireUtc,
    string State);
