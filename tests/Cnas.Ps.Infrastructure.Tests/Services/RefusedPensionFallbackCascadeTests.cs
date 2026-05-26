using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using Cnas.Ps.Infrastructure.Workflow;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// R0942 / TOR §10.1 — integration tests for
/// <see cref="RefusedPensionFallbackCascade"/>. Uses EF Core InMemory + NSubstitute
/// for the surrounding collaborators. The pension-detection heuristic is
/// substring-match on the passport Code (<c>"PENSION"</c> / <c>"PENSIE"</c>).
/// </summary>
public sealed class RefusedPensionFallbackCascadeTests
{
    private static readonly DateTime ClockNow = new(2026, 5, 25, 11, 0, 0, DateTimeKind.Utc);

    private const string TargetCode = "SP-3.2-N-SOCIAL-ALLOWANCE-ELDERLY";

    private static CnasDbContext CreateContext() =>
        new(
            new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-cascade-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options);

    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }

    private sealed record Harness(
        CnasDbContext Db,
        RefusedPensionFallbackCascade Sut,
        IAuditService Audit);

    private static Harness Create(WorkflowOptions? opts = null)
    {
        var db = CreateContext();
        var sqids = Substitute.For<ISqidService>();
        sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");

        var audit = Substitute.For<IAuditService>();
        audit.RecordAsync(
                Arg.Any<string>(), Arg.Any<AuditSeverity>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));

        var caller = Substitute.For<ICallerContext>();
        caller.UserSqid.Returns("SQID-CALLER");
        caller.SourceIp.Returns("127.0.0.1");
        caller.CorrelationId.Returns("corr-1");

        var monitor = Substitute.For<IOptionsMonitor<WorkflowOptions>>();
        monitor.CurrentValue.Returns(opts ?? new WorkflowOptions
        {
            AutoFallbackToSocialAllowance = true,
            SocialAllowancePassportCode = TargetCode,
        });

        var sut = new RefusedPensionFallbackCascade(
            db, new StubClock(ClockNow), sqids, caller, audit, monitor);
        return new Harness(db, sut, audit);
    }

    /// <summary>Seeds a Solicitant + a passport + an active Rejected application referencing the passport.</summary>
    private static async Task<(long appId, ServicePassport passport, Solicitant solicitant)> SeedRejectedAsync(
        Harness h, string passportCode, ApplicationStatus status = ApplicationStatus.Rejected)
    {
        var solicitant = new Solicitant
        {
            CreatedAtUtc = ClockNow,
            NationalId = "2000123456782",
            Kind = ApplicantKind.NaturalPerson,
            DisplayName = "Ion Popescu",
            PreferredLanguage = "ro",
            IsActive = true,
        };
        h.Db.Solicitants.Add(solicitant);

        var passport = new ServicePassport
        {
            CreatedAtUtc = ClockNow,
            Code = passportCode,
            NameRo = "Test passport",
            DescriptionRo = "Test",
            FormSchemaJson = "{}",
            WorkflowCode = "WF-TEST",
            MaxProcessingDays = 30,
            IsEnabled = true,
            IsActive = true,
        };
        h.Db.ServicePassports.Add(passport);
        await h.Db.SaveChangesAsync();

        var app = new ServiceApplication
        {
            CreatedAtUtc = ClockNow,
            SolicitantId = solicitant.Id,
            ServicePassportId = passport.Id,
            Status = status,
            FormPayloadJson = "{\"someField\":\"value\"}",
            SnapshotJson = "{}",
            SubmittedAtUtc = ClockNow.AddDays(-1),
            IsActive = true,
        };
        h.Db.Applications.Add(app);
        await h.Db.SaveChangesAsync();

        return (app.Id, passport, solicitant);
    }

    private static async Task SeedTargetPassportAsync(Harness h, string code = TargetCode)
    {
        h.Db.ServicePassports.Add(new ServicePassport
        {
            CreatedAtUtc = ClockNow,
            Code = code,
            NameRo = "Alocație socială vârstnici",
            DescriptionRo = "Target",
            FormSchemaJson = "{}",
            WorkflowCode = "WF-SOCIAL-ALLOWANCE",
            MaxProcessingDays = 30,
            IsEnabled = true,
            IsActive = true,
            Version = 1,
        });
        await h.Db.SaveChangesAsync();
    }

    /// <summary>Non-pension refusal returns NOT_A_PENSION_REFUSAL and creates no draft.</summary>
    [Fact]
    public async Task EvaluateAsync_NonPensionRefusal_ReturnsNotPensionRefusal()
    {
        var h = Create();
        var (appId, _, _) = await SeedRejectedAsync(h, "SP-3.6-A-UNEMPLOYMENT-ALLOWANCE");
        await SeedTargetPassportAsync(h);

        var result = await h.Sut.EvaluateAsync(appId);

        result.IsSuccess.Should().BeTrue();
        result.Value.WasCascadeTriggered.Should().BeFalse();
        result.Value.ReasonCode.Should().Be(RefusedPensionFallbackCascade.ReasonNotPension);
        (await h.Db.Applications.CountAsync()).Should().Be(1);
    }

    /// <summary>Pension refusal creates a Draft follow-up application referencing the target passport.</summary>
    [Fact]
    public async Task EvaluateAsync_PensionRefusal_CreatesAlocatieSocialaDraft()
    {
        var h = Create();
        var (appId, _, solicitant) = await SeedRejectedAsync(h, "SP-3.2-A-OLD-AGE-PENSION");
        await SeedTargetPassportAsync(h);

        var result = await h.Sut.EvaluateAsync(appId);

        result.IsSuccess.Should().BeTrue();
        result.Value.WasCascadeTriggered.Should().BeTrue();
        result.Value.ReasonCode.Should().Be(RefusedPensionFallbackCascade.ReasonFallbackInitiated);
        result.Value.FallbackApplicationSqid.Should().NotBeNull();

        var draft = await h.Db.Applications.Where(a => a.Id != appId).SingleAsync();
        draft.SolicitantId.Should().Be(solicitant.Id);
        draft.Status.Should().Be(ApplicationStatus.Draft);
        draft.FormPayloadJson.Should().Contain("cascadeFromDecisionId");
    }

    /// <summary>Feature disabled: no draft is created and the reason is FEATURE_DISABLED.</summary>
    [Fact]
    public async Task EvaluateAsync_FeatureDisabled_ShortCircuits()
    {
        var opts = new WorkflowOptions
        {
            AutoFallbackToSocialAllowance = false,
            SocialAllowancePassportCode = TargetCode,
        };
        var h = Create(opts);
        var (appId, _, _) = await SeedRejectedAsync(h, "SP-3.2-A-OLD-AGE-PENSION");
        await SeedTargetPassportAsync(h);

        var result = await h.Sut.EvaluateAsync(appId);

        result.IsSuccess.Should().BeTrue();
        result.Value.WasCascadeTriggered.Should().BeFalse();
        result.Value.ReasonCode.Should().Be(RefusedPensionFallbackCascade.ReasonFeatureDisabled);
        (await h.Db.Applications.CountAsync()).Should().Be(1);
    }

    /// <summary>Calling twice for the same refused decision yields ALREADY_CASCADED on the second call.</summary>
    [Fact]
    public async Task EvaluateAsync_AlreadyCascaded_ReturnsIdempotentResult()
    {
        var h = Create();
        var (appId, _, _) = await SeedRejectedAsync(h, "SP-3.2-A-OLD-AGE-PENSION");
        await SeedTargetPassportAsync(h);

        var first = await h.Sut.EvaluateAsync(appId);
        first.Value.WasCascadeTriggered.Should().BeTrue();

        var second = await h.Sut.EvaluateAsync(appId);
        second.IsSuccess.Should().BeTrue();
        second.Value.WasCascadeTriggered.Should().BeFalse();
        second.Value.ReasonCode.Should().Be(RefusedPensionFallbackCascade.ReasonAlreadyCascaded);
        // Still only the original + one cascade draft.
        (await h.Db.Applications.CountAsync()).Should().Be(2);
    }

    /// <summary>Successful cascade emits a Notice DECISION.FALLBACK_INITIATED audit row.</summary>
    [Fact]
    public async Task EvaluateAsync_OnSuccessfulCascade_EmitsAuditRow()
    {
        var h = Create();
        var (appId, _, _) = await SeedRejectedAsync(h, "SP-3.2-A-OLD-AGE-PENSION");
        await SeedTargetPassportAsync(h);

        var result = await h.Sut.EvaluateAsync(appId);

        result.IsSuccess.Should().BeTrue();
        await h.Audit.Received(1).RecordAsync(
            RefusedPensionFallbackCascade.AuditFallbackInitiated,
            AuditSeverity.Notice,
            Arg.Any<string>(),
            nameof(ServiceApplication),
            Arg.Any<long?>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// iter-149 — re-cascade against a soft-deleted prior cascade draft is still
    /// blocked. The idempotency check now ignores the cascade-draft's IsActive
    /// flag so an admin tidy-up (or citizen withdrawal) of the first cascade
    /// cannot trigger a duplicate fallback on the same refused decision.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_AlreadyCascaded_BlocksEvenAfterPriorDraftSoftDeleted()
    {
        var h = Create();
        var (appId, _, _) = await SeedRejectedAsync(h, "SP-3.2-A-OLD-AGE-PENSION");
        await SeedTargetPassportAsync(h);

        var first = await h.Sut.EvaluateAsync(appId);
        first.IsSuccess.Should().BeTrue();
        first.Value.WasCascadeTriggered.Should().BeTrue();

        // Soft-delete the first cascade draft (the marker stays in the payload).
        var draft = await h.Db.Applications.SingleAsync(a => a.Id != appId);
        draft.IsActive = false;
        await h.Db.SaveChangesAsync();

        var second = await h.Sut.EvaluateAsync(appId);

        second.IsSuccess.Should().BeTrue();
        second.Value.WasCascadeTriggered.Should().BeFalse();
        second.Value.ReasonCode.Should().Be(RefusedPensionFallbackCascade.ReasonAlreadyCascaded);
        // Still only the original + the (now soft-deleted) first cascade draft.
        (await h.Db.Applications.CountAsync()).Should().Be(2);
    }

    /// <summary>Target passport missing: cascade reports TARGET_PASSPORT_MISSING without creating any draft.</summary>
    [Fact]
    public async Task EvaluateAsync_TargetPassportMissing_ReturnsTargetPassportMissing()
    {
        var h = Create();
        var (appId, _, _) = await SeedRejectedAsync(h, "SP-3.2-A-OLD-AGE-PENSION");
        // Intentionally do NOT seed the target passport.

        var result = await h.Sut.EvaluateAsync(appId);

        result.IsSuccess.Should().BeTrue();
        result.Value.WasCascadeTriggered.Should().BeFalse();
        result.Value.ReasonCode.Should().Be(RefusedPensionFallbackCascade.ReasonTargetMissing);
        (await h.Db.Applications.CountAsync()).Should().Be(1);
    }
}
