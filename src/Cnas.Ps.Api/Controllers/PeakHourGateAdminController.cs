using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.Scheduling;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Scheduling;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R2173 / TOR PSR 004 — admin REST surface over the peak-hour gate
/// configuration. Restricted to the
/// <see cref="AuthorizationComposition.CnasAdmin"/> policy because flipping
/// the global override bypasses every <c>OffPeakOnly</c> profile.
/// </summary>
/// <remarks>
/// <para>
/// <b>Route table.</b>
/// <list type="bullet">
///   <item><c>GET  /api/admin/peak-hour-gate/status</c>  — current options + per-job decisions.</item>
///   <item><c>POST /api/admin/peak-hour-gate/override</c> — flip the global override (Critical audit).</item>
/// </list>
/// </para>
/// <para>
/// <b>Why not service-layer.</b> The override toggle has no business logic
/// beyond the boolean flip — the audit write + gate-state mutation are both
/// trivial. Hoisting them into a dedicated Application service would add
/// indirection without value. The controller calls <see cref="IAuditService"/>
/// directly (the service is registered Scoped) and the
/// <see cref="PeakHourGateOverrideStore"/> singleton holds the runtime state.
/// </para>
/// </remarks>
/// <param name="gate">Peak-hour gate consulted for per-job preview decisions.</param>
/// <param name="options">Live options snapshot — exposed in the status DTO.</param>
/// <param name="overrideStore">Runtime override toggle mutated by the POST endpoint.</param>
/// <param name="audit">Audit service used for the Critical override row.</param>
/// <param name="clock">UTC clock used to stamp the status DTO timestamp.</param>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasAdmin)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/admin/peak-hour-gate")]
public sealed class PeakHourGateAdminController(
    IPeakHourGate gate,
    IOptionsMonitor<PeakHourGateOptions> options,
    PeakHourGateOverrideStore overrideStore,
    IAuditService audit,
    ICnasTimeProvider clock) : ControllerBase
{
    private readonly IPeakHourGate _gate = gate;
    private readonly IOptionsMonitor<PeakHourGateOptions> _options = options;
    private readonly PeakHourGateOverrideStore _overrideStore = overrideStore;
    private readonly IAuditService _audit = audit;
    private readonly ICnasTimeProvider _clock = clock;

    /// <summary>
    /// Returns the current peak-hour gate configuration plus the gate's
    /// preview decision for every known job code at the current instant. The
    /// preview lets operators confirm — before flipping the override — which
    /// jobs are currently being skipped.
    /// </summary>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with a <see cref="PeakHourGateStatusDto"/>.</returns>
    [HttpGet("status")]
    public async Task<ActionResult<PeakHourGateStatusDto>> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        var opts = _options.CurrentValue;
        var decisions = new Dictionary<string, string>(JobScheduleProfileRegistry.Defaults.Count,
            System.StringComparer.Ordinal);
        foreach (var kv in JobScheduleProfileRegistry.Defaults)
        {
            var decision = await _gate.EvaluateAsync(kv.Key, cancellationToken).ConfigureAwait(false);
            decisions[kv.Key] = decision == PeakHourGateDecision.Allow ? "Allow" : "Skip";
        }

        var dto = new PeakHourGateStatusDto(
            OffPeakStartLocalHour: opts.OffPeakStartLocalHour,
            OffPeakEndLocalHour: opts.OffPeakEndLocalHour,
            GlobalOverride: opts.GlobalOverride || _overrideStore.IsOverrideActive(),
            EvaluatedAtLocal: _clock.UtcNow,
            Decisions: decisions);
        return Ok(dto);
    }

    /// <summary>
    /// Flips the runtime global-override toggle. Subsequent gate evaluations
    /// honour the new value. Emits a <c>PEAK_HOUR_GATE.OVERRIDDEN</c> Critical
    /// audit row capturing the new state.
    /// </summary>
    /// <param name="input">Target override state.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the new <see cref="PeakHourGateStatusDto"/>.</returns>
    [HttpPost("override")]
    [Consumes("application/json")]
    public async Task<ActionResult<PeakHourGateStatusDto>> SetOverrideAsync(
        [FromBody] PeakHourGateOverrideInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        _overrideStore.SetOverride(input.Enabled);

        var details = JsonSerializer.Serialize(new
        {
            enabled = input.Enabled,
        });
        await _audit.RecordAsync(
            eventCode: "PEAK_HOUR_GATE.OVERRIDDEN",
            severity: AuditSeverity.Critical,
            actorId: User?.Identity?.Name ?? "admin",
            targetEntity: "PeakHourGate",
            targetEntityId: null,
            detailsJson: details,
            sourceIp: HttpContext?.Connection?.RemoteIpAddress?.ToString(),
            correlationId: HttpContext?.TraceIdentifier,
            cancellationToken).ConfigureAwait(false);

        return await GetStatusAsync(cancellationToken).ConfigureAwait(false);
    }
}
