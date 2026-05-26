using Cnas.Ps.Api.Composition;
using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.Scheduling;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Scheduling;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// R2173 / TOR PSR 004 — tests for <see cref="PeakHourGateAdminController"/>.
/// Validates the authorize-policy gate (cnas-admin only via the
/// <see cref="AuthorizationComposition.CnasAdmin"/> policy attribute), the
/// status DTO shape, and the Critical audit emission on override flip.
/// </summary>
public sealed class PeakHourGateAdminControllerTests
{
    /// <summary>Deterministic UTC clock instant used by every assertion.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 23, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Controller_HasCnasAdminAuthorizationPolicy()
    {
        // R2173 — the override toggle bypasses every OffPeakOnly profile, so
        // the controller MUST be restricted to CnasAdmin. A 3-role caller
        // (CnasUser / CnasDecider / CnasTechAdmin) hitting it directly would
        // surface as 403 via ASP.NET — encoded here as a reflection assertion
        // on the [Authorize(Policy=...)] attribute.
        var attrs = typeof(PeakHourGateAdminController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>()
            .ToList();

        attrs.Should().NotBeEmpty(
            "the peak-hour-gate admin controller MUST be gated by an explicit [Authorize] attribute.");
        attrs.Should().Contain(a => a.Policy == AuthorizationComposition.CnasAdmin,
            "the policy must be CnasAdmin so only cnas-admin users reach this surface (a non-admin caller therefore receives 403).");
    }

    [Fact]
    public async Task GetStatus_ReturnsDecisionDictionaryForEveryKnownJob()
    {
        var harness = BuildHarness();

        var result = await harness.Controller.GetStatusAsync(CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var dto = ok.Value.Should().BeOfType<PeakHourGateStatusDto>().Subject;
        dto.Decisions.Should().HaveCount(JobScheduleProfileRegistry.Defaults.Count);
        dto.Decisions.Should().ContainKey(JobScheduleProfileRegistry.KpiSnapshot);
        dto.OffPeakStartLocalHour.Should().Be(22);
        dto.OffPeakEndLocalHour.Should().Be(6);
    }

    [Fact]
    public async Task PostOverride_Enabled_ReturnsOkAndEmitsCriticalAudit()
    {
        var harness = BuildHarness();

        var result = await harness.Controller.SetOverrideAsync(
            new PeakHourGateOverrideInput(Enabled: true),
            CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        await harness.Audit.Received(1).RecordAsync(
            "PEAK_HOUR_GATE.OVERRIDDEN",
            AuditSeverity.Critical,
            Arg.Any<string>(),
            "PeakHourGate",
            Arg.Any<long?>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
        harness.OverrideStore.IsOverrideActive().Should().BeTrue();
    }

    [Fact]
    public async Task PostOverride_Disabled_FlipsBackToFalse()
    {
        var harness = BuildHarness(initialOverride: true);

        await harness.Controller.SetOverrideAsync(
            new PeakHourGateOverrideInput(Enabled: false),
            CancellationToken.None);

        harness.OverrideStore.IsOverrideActive().Should().BeFalse();
    }

    // ─────────────────────── helpers ───────────────────────

    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }

    private sealed class Harness
    {
        public required PeakHourGateAdminController Controller { get; init; }
        public required IAuditService Audit { get; init; }
        public required PeakHourGateOverrideStore OverrideStore { get; init; }
    }

    private static Harness BuildHarness(bool initialOverride = false)
    {
        var gate = Substitute.For<IPeakHourGate>();
        gate.EvaluateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PeakHourGateDecision.Allow));

        var monitor = Substitute.For<IOptionsMonitor<PeakHourGateOptions>>();
        monitor.CurrentValue.Returns(new PeakHourGateOptions
        {
            OffPeakStartLocalHour = 22,
            OffPeakEndLocalHour = 6,
            GlobalOverride = false,
        });

        var overrideStore = new PeakHourGateOverrideStore(initialOverride);

        var audit = Substitute.For<IAuditService>();
        audit.RecordAsync(
            Arg.Any<string>(), Arg.Any<AuditSeverity>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));

        var clock = new StubClock(ClockNow);
        var controller = new PeakHourGateAdminController(gate, monitor, overrideStore, audit, clock)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext(),
            },
        };

        return new Harness
        {
            Controller = controller,
            Audit = audit,
            OverrideStore = overrideStore,
        };
    }
}
