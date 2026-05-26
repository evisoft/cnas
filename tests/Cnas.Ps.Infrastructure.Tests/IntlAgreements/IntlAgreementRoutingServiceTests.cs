using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.IntlAgreements;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.IntlAgreements;
using Cnas.Ps.Infrastructure.Services.IntlAgreements.Policies;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.IntlAgreements;

/// <summary>
/// R1201 / R1402 / TOR §3.4-B / §3.6-C — service-level tests for the
/// international-agreements 3-level routing engine. Exercises the
/// Draft → Submitted → Local/Regional/National → Approved/Rejected/Revision
/// state machine, the per-level reviewer-role guard, audit + metric
/// emission, and the auto-generated case-number format.
/// </summary>
public sealed class IntlAgreementRoutingServiceTests
{
    /// <summary>Fixed UTC clock used by every test.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 23, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Stub clock returning the fixed instant.</summary>
    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        /// <inheritdoc />
        public DateTime UtcNow { get; } = now;
    }

    /// <summary>Builds a fresh EF Core InMemory context.</summary>
    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-intl-agreement-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    /// <summary>Sqid stub that round-trips "IAR-{id}" strings.</summary>
    private static ISqidService NewSqidMock()
    {
        var sqids = Substitute.For<ISqidService>();
        sqids.Encode(Arg.Any<long>()).Returns(call => $"IAR-{call.Arg<long>()}");
        sqids.TryDecode(Arg.Any<string>()).Returns(call =>
        {
            var s = call.Arg<string>();
            if (s is not null && s.StartsWith("IAR-", StringComparison.Ordinal)
                && long.TryParse(s["IAR-".Length..], out var id))
            {
                return Result<long>.Success(id);
            }
            return Result<long>.Failure(ErrorCodes.InvalidSqid, "bad sqid");
        });
        return sqids;
    }

    /// <summary>Captures audit invocations for assertion.</summary>
    private static (IAuditService Audit, Func<List<(string Code, AuditSeverity Severity, long? TargetId)>> Calls)
        NewAuditCapture()
    {
        var calls = new List<(string Code, AuditSeverity Severity, long? TargetId)>();
        var audit = Substitute.For<IAuditService>();
        audit.RecordAsync(
                Arg.Any<string>(),
                Arg.Any<AuditSeverity>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<long?>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                calls.Add((
                    call.ArgAt<string>(0),
                    call.ArgAt<AuditSeverity>(1),
                    call.ArgAt<long?>(4)));
                return Task.FromResult(Result.Success());
            });
        return (audit, () => calls);
    }

    /// <summary>Authenticated-caller stub carrying the supplied set of role codes.</summary>
    private static ICallerContext NewCaller(params string[] roles)
    {
        var caller = Substitute.For<ICallerContext>();
        caller.UserId.Returns(7L);
        caller.UserSqid.Returns("USR-7");
        caller.SourceIp.Returns("203.0.113.7");
        caller.CorrelationId.Returns("corr-intl");
        caller.Roles.Returns((IReadOnlyCollection<string>)roles);
        return caller;
    }

    /// <summary>Standard policy collection (incapacity + unemployment).</summary>
    private static IEnumerable<IIntlAgreementRoutingPolicy> NewPolicies() =>
        [
            new IncapacityMaternityIntlAgreementRoutingPolicy(),
            new UnemploymentIntlAgreementRoutingPolicy(),
        ];

    /// <summary>Builds the SUT with the supplied caller (defaults to no roles).</summary>
    private static IntlAgreementRoutingService NewService(
        CnasDbContext db,
        IAuditService audit,
        ICallerContext? caller = null)
    {
        var clock = new StubClock(ClockNow);
        return new(
            db,
            clock,
            NewSqidMock(),
            caller ?? NewCaller(),
            audit,
            IdHashHelper.Instance,
            NewPolicies(),
            new IntlAgreementReviewCaseCreateInputValidator(),
            new IntlAgreementReviewInputValidator(),
            new IntlAgreementReviewCaseResubmitInputValidator(),
            new IntlAgreementReviewCaseReasonInputValidator(),
            new IntlAgreementReviewCaseFilterValidator());
    }

    /// <summary>Canonical create input.</summary>
    private static IntlAgreementReviewCaseCreateInputDto BuildCreateInput(
        IntlAgreementBenefitKind kind = IntlAgreementBenefitKind.IncapacityMaternity,
        string idnp = "2002000000007",
        string agreement = "RO_MD_2006",
        string host = "RO") => new(
            BenefitKind: kind.ToString(),
            BeneficiaryIdnp: idnp,
            BeneficiaryDisplayName: "Ion Popescu",
            AgreementCode: agreement,
            HostCountryCode: host);

    [Fact]
    public async Task CreateAsync_HappyPath_PersistsDraftWithGeneratedCaseNumberAndAudit()
    {
        var db = CreateContext();
        var (audit, calls) = NewAuditCapture();
        var sut = NewService(db, audit);

        var result = await sut.CreateAsync(BuildCreateInput());

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(nameof(IntlAgreementReviewCaseStatus.Draft));
        result.Value.CurrentLevel.Should().Be(nameof(IntlAgreementReviewLevel.Local));
        result.Value.CaseNumber.Should().Be("IAR-2026-000001");
        result.Value.BeneficiaryIdnpHash.Should().NotBeNullOrWhiteSpace();
        (await db.IntlAgreementReviewCases.CountAsync()).Should().Be(1);
        calls().Should().ContainSingle(c =>
            c.Code == IntlAgreementRoutingService.AuditCreated
            && c.Severity == AuditSeverity.Critical);
    }

    [Fact]
    public async Task SubmitAsync_FromNonDraft_ReturnsConflict()
    {
        var db = CreateContext();
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, audit);
        var created = await sut.CreateAsync(BuildCreateInput());
        await sut.SubmitAsync(created.Value.Id);

        var second = await sut.SubmitAsync(created.Value.Id);

        second.IsFailure.Should().BeTrue();
        second.ErrorCode.Should().Be(ErrorCodes.Conflict);
        second.ErrorMessage.Should().Contain("INVALID_TRANSITION");
    }

    [Fact]
    public async Task SubmitAsync_FromDraft_TransitionsToAtLocalReview()
    {
        var db = CreateContext();
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, audit);
        var created = await sut.CreateAsync(BuildCreateInput());

        var result = await sut.SubmitAsync(created.Value.Id);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(nameof(IntlAgreementReviewCaseStatus.AtLocalReview));
        result.Value.CurrentLevel.Should().Be(nameof(IntlAgreementReviewLevel.Local));
        result.Value.SubmittedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task RecordReviewAsync_WrongRole_ReturnsForbidden()
    {
        var db = CreateContext();
        var (audit, _) = NewAuditCapture();
        // The caller is a generic admin but lacks the IMR_LOCAL_OFFICE_REVIEWER role.
        var caller = NewCaller("cnas-admin");
        var sut = NewService(db, audit, caller);
        var created = await sut.CreateAsync(BuildCreateInput());
        await sut.SubmitAsync(created.Value.Id);

        var review = await sut.RecordReviewAsync(
            created.Value.Id,
            new IntlAgreementReviewInputDto(
                Outcome: nameof(IntlAgreementReviewStepOutcome.Approved),
                Note: "Tried to approve without the right role."));

        review.IsFailure.Should().BeTrue();
        review.ErrorCode.Should().Be(ErrorCodes.Forbidden);
        review.ErrorMessage.Should().Contain("WRONG_REVIEWER_ROLE");
    }

    [Fact]
    public async Task RecordReviewAsync_LocalApproved_AdvancesToRegional()
    {
        var db = CreateContext();
        var (audit, _) = NewAuditCapture();
        var caller = NewCaller(IncapacityMaternityIntlAgreementRoutingPolicy.LocalRole);
        var sut = NewService(db, audit, caller);
        var created = await sut.CreateAsync(BuildCreateInput());
        await sut.SubmitAsync(created.Value.Id);

        var review = await sut.RecordReviewAsync(
            created.Value.Id,
            new IntlAgreementReviewInputDto(
                Outcome: nameof(IntlAgreementReviewStepOutcome.Approved),
                Note: "File complete at local office."));

        review.IsSuccess.Should().BeTrue();
        review.Value.Status.Should().Be(nameof(IntlAgreementReviewCaseStatus.AtRegionalReview));
        review.Value.CurrentLevel.Should().Be(nameof(IntlAgreementReviewLevel.Regional));
        review.Value.Steps.Should().HaveCount(1);
        review.Value.Steps[0].Level.Should().Be(nameof(IntlAgreementReviewLevel.Local));
        review.Value.Steps[0].Outcome.Should().Be(nameof(IntlAgreementReviewStepOutcome.Approved));
    }

    [Fact]
    public async Task RecordReviewAsync_AllThreeApprovals_FinalisesAsApproved()
    {
        var db = CreateContext();
        var (audit, _) = NewAuditCapture();
        var caller = NewCaller(
            IncapacityMaternityIntlAgreementRoutingPolicy.LocalRole,
            IncapacityMaternityIntlAgreementRoutingPolicy.RegionalRole,
            IncapacityMaternityIntlAgreementRoutingPolicy.NationalRole);
        var sut = NewService(db, audit, caller);
        var created = await sut.CreateAsync(BuildCreateInput());
        await sut.SubmitAsync(created.Value.Id);
        var sqid = created.Value.Id;

        await sut.RecordReviewAsync(sqid, new IntlAgreementReviewInputDto(
            Outcome: nameof(IntlAgreementReviewStepOutcome.Approved),
            Note: "Local approved."));
        await sut.RecordReviewAsync(sqid, new IntlAgreementReviewInputDto(
            Outcome: nameof(IntlAgreementReviewStepOutcome.Approved),
            Note: "Regional approved."));
        var third = await sut.RecordReviewAsync(sqid, new IntlAgreementReviewInputDto(
            Outcome: nameof(IntlAgreementReviewStepOutcome.Approved),
            Note: "National approved."));

        third.IsSuccess.Should().BeTrue();
        third.Value.Status.Should().Be(nameof(IntlAgreementReviewCaseStatus.Approved));
        third.Value.CurrentLevel.Should().Be(nameof(IntlAgreementReviewLevel.Complete));
        third.Value.ApprovedAt.Should().NotBeNull();
        third.Value.Steps.Should().HaveCount(3);
    }

    [Fact]
    public async Task RecordReviewAsync_RejectedAtRegional_TerminatesAsRejected()
    {
        var db = CreateContext();
        var (audit, _) = NewAuditCapture();
        var caller = NewCaller(
            IncapacityMaternityIntlAgreementRoutingPolicy.LocalRole,
            IncapacityMaternityIntlAgreementRoutingPolicy.RegionalRole);
        var sut = NewService(db, audit, caller);
        var created = await sut.CreateAsync(BuildCreateInput());
        await sut.SubmitAsync(created.Value.Id);
        await sut.RecordReviewAsync(created.Value.Id, new IntlAgreementReviewInputDto(
            Outcome: nameof(IntlAgreementReviewStepOutcome.Approved),
            Note: "Local approved."));

        var rejection = await sut.RecordReviewAsync(
            created.Value.Id,
            new IntlAgreementReviewInputDto(
                Outcome: nameof(IntlAgreementReviewStepOutcome.Rejected),
                Note: "Missing required affidavit at regional review."));

        rejection.IsSuccess.Should().BeTrue();
        rejection.Value.Status.Should().Be(nameof(IntlAgreementReviewCaseStatus.Rejected));
        rejection.Value.RejectedAt.Should().NotBeNull();
        rejection.Value.RejectionReason.Should().Contain("Missing required affidavit");
    }

    [Fact]
    public async Task RecordReviewAsync_RevisionRequested_MovesToRevisionRequested()
    {
        var db = CreateContext();
        var (audit, _) = NewAuditCapture();
        var caller = NewCaller(IncapacityMaternityIntlAgreementRoutingPolicy.LocalRole);
        var sut = NewService(db, audit, caller);
        var created = await sut.CreateAsync(BuildCreateInput());
        await sut.SubmitAsync(created.Value.Id);

        var revision = await sut.RecordReviewAsync(
            created.Value.Id,
            new IntlAgreementReviewInputDto(
                Outcome: nameof(IntlAgreementReviewStepOutcome.RevisionRequested),
                Note: "Need translated marriage certificate."));

        revision.IsSuccess.Should().BeTrue();
        revision.Value.Status.Should().Be(nameof(IntlAgreementReviewCaseStatus.RevisionRequested));
        revision.Value.CurrentLevel.Should().Be(nameof(IntlAgreementReviewLevel.RevisionRequired));
        revision.Value.RevisionRequestNote.Should().Contain("translated marriage certificate");
    }

    [Fact]
    public async Task ResubmitAsync_AfterRevisionRequest_RestartsAtLocal()
    {
        var db = CreateContext();
        var (audit, _) = NewAuditCapture();
        var caller = NewCaller(IncapacityMaternityIntlAgreementRoutingPolicy.LocalRole);
        var sut = NewService(db, audit, caller);
        var created = await sut.CreateAsync(BuildCreateInput());
        await sut.SubmitAsync(created.Value.Id);
        await sut.RecordReviewAsync(created.Value.Id, new IntlAgreementReviewInputDto(
            Outcome: nameof(IntlAgreementReviewStepOutcome.RevisionRequested),
            Note: "Please attach proof of stay."));

        var resubmit = await sut.ResubmitAsync(
            created.Value.Id,
            new IntlAgreementReviewCaseResubmitInputDto(
                Note: "Updated evidence attached.",
                EvidenceJson: "{\"proof\":\"present\"}"));

        resubmit.IsSuccess.Should().BeTrue();
        resubmit.Value.Status.Should().Be(nameof(IntlAgreementReviewCaseStatus.AtLocalReview));
        resubmit.Value.CurrentLevel.Should().Be(nameof(IntlAgreementReviewLevel.Local));
        resubmit.Value.EvidenceJson.Should().Contain("proof");
        resubmit.Value.RevisionRequestNote.Should().BeNull();
    }

    [Fact]
    public async Task ToDto_StepsOrdered_ByReviewedAtAscending()
    {
        var db = CreateContext();
        var (audit, _) = NewAuditCapture();
        var caller = NewCaller(
            IncapacityMaternityIntlAgreementRoutingPolicy.LocalRole,
            IncapacityMaternityIntlAgreementRoutingPolicy.RegionalRole);
        var sut = NewService(db, audit, caller);
        var created = await sut.CreateAsync(BuildCreateInput());
        await sut.SubmitAsync(created.Value.Id);
        await sut.RecordReviewAsync(created.Value.Id, new IntlAgreementReviewInputDto(
            Outcome: nameof(IntlAgreementReviewStepOutcome.Approved),
            Note: "Local OK."));
        await sut.RecordReviewAsync(created.Value.Id, new IntlAgreementReviewInputDto(
            Outcome: nameof(IntlAgreementReviewStepOutcome.Approved),
            Note: "Regional OK."));

        var get = await sut.GetByIdAsync(created.Value.Id);

        get.IsSuccess.Should().BeTrue();
        get.Value.Steps.Should().HaveCount(2);
        get.Value.Steps[0].Level.Should().Be(nameof(IntlAgreementReviewLevel.Local));
        get.Value.Steps[1].Level.Should().Be(nameof(IntlAgreementReviewLevel.Regional));
    }

    [Fact]
    public async Task ListAsync_FilteredByBenefitKind_ReturnsOnlyMatching()
    {
        var db = CreateContext();
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, audit);
        await sut.CreateAsync(BuildCreateInput(IntlAgreementBenefitKind.IncapacityMaternity, "2002000000007"));
        await sut.CreateAsync(BuildCreateInput(IntlAgreementBenefitKind.Unemployment, "2002000000015"));

        var page = await sut.ListAsync(new IntlAgreementReviewCaseFilterDto(
            BenefitKind: nameof(IntlAgreementBenefitKind.Unemployment)));

        page.IsSuccess.Should().BeTrue();
        page.Value.Items.Should().HaveCount(1);
        page.Value.Items[0].BenefitKind.Should().Be(nameof(IntlAgreementBenefitKind.Unemployment));
    }

    [Fact]
    public async Task CancelAsync_FromNonTerminal_TerminatesCancelled()
    {
        var db = CreateContext();
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, audit);
        var created = await sut.CreateAsync(BuildCreateInput());

        var cancel = await sut.CancelAsync(
            created.Value.Id,
            new IntlAgreementReviewCaseReasonInputDto(Reason: "Beneficiary withdrew application."));

        cancel.IsSuccess.Should().BeTrue();
        cancel.Value.Status.Should().Be(nameof(IntlAgreementReviewCaseStatus.Cancelled));
        cancel.Value.CancelReason.Should().Contain("withdrew");
    }
}
