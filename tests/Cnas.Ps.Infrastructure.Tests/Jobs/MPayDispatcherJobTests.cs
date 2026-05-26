using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Jobs;
using Cnas.Ps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Quartz;

namespace Cnas.Ps.Infrastructure.Tests.Jobs;

/// <summary>
/// Unit tests for <see cref="MPayDispatcherJob"/>. The job dispatches outbound MPay
/// payments for approved applications whose dossier has closed and whose
/// <c>PaymentDispatchedAtUtc</c> is still null. Idempotency guarantees: a row is
/// stamped on success and skipped on subsequent runs; on MPay failure the row is left
/// for the next run.
/// </summary>
public class MPayDispatcherJobTests
{
    private static readonly DateTime ClockNow = new(2026, 5, 19, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Execute_AppNotEligibleForDispatch_SkipsRow()
    {
        // Dossier not closed → ineligible. Expectation: MPay not invoked.
        var harness = Harness.Create();
        await harness.SeedDispatchableAsync(closeDossier: false);

        await harness.Job.Execute(FakeContext());

        await harness.MPay.DidNotReceive().SendAsync(
            Arg.Any<MPayOutbound>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Execute_HappyPath_DispatchesAndStampsApp()
    {
        var harness = Harness.Create();
        var seeded = await harness.SeedDispatchableAsync(closeDossier: true);
        harness.MPay
            .SendAsync(Arg.Any<MPayOutbound>(), Arg.Any<CancellationToken>())
            .Returns(Result<MPayReceipt>.Success(new MPayReceipt("TX-12345", "ACCEPTED")));

        await harness.Job.Execute(FakeContext());

        var app = await harness.Db.Applications.SingleAsync(a => a.Id == seeded.AppId);
        app.PaymentDispatchedAtUtc.Should().Be(ClockNow);
        app.PaymentTransactionId.Should().Be("TX-12345");
        app.PaymentStatus.Should().Be("ACCEPTED");

        await harness.MPay.Received(1).SendAsync(
            Arg.Is<MPayOutbound>(p =>
                p.BeneficiaryIdnp == seeded.NationalId &&
                p.BeneficiaryIban == seeded.Iban &&
                p.AmountMdl == seeded.Amount &&
                p.Reference.Contains(seeded.AppReference!) &&
                p.Reference.Contains(seeded.DossierNumber)),
            Arg.Any<CancellationToken>());

        await harness.Audit.Received(1).RecordAsync(
            "PAYMENT.DISPATCHED",
            AuditSeverity.Critical,
            Arg.Any<string>(),
            Arg.Any<string?>(),
            seeded.AppId,
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Execute_MPayFailure_LeavesAppForRetry()
    {
        var harness = Harness.Create();
        var seeded = await harness.SeedDispatchableAsync(closeDossier: true);
        harness.MPay
            .SendAsync(Arg.Any<MPayOutbound>(), Arg.Any<CancellationToken>())
            .Returns(Result<MPayReceipt>.Failure(ErrorCodes.MPayFailed, "upstream 503"));

        await harness.Job.Execute(FakeContext());

        // Row stays untouched so the next run can retry — idempotent retry semantics.
        var app = await harness.Db.Applications.SingleAsync(a => a.Id == seeded.AppId);
        app.PaymentDispatchedAtUtc.Should().BeNull();
        app.PaymentTransactionId.Should().BeNull();
        app.PaymentStatus.Should().BeNull();

        await harness.Audit.DidNotReceive().RecordAsync(
            "PAYMENT.DISPATCHED",
            Arg.Any<AuditSeverity>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<long?>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Closes the wire-up between <c>ApplicationProcessingService</c> and this job: when the
    /// dossier carries a computed amount, the dispatcher must forward exactly that decimal
    /// (in MDL) to MPay along with the solicitant's IBAN.
    /// </summary>
    [Fact]
    public async Task Execute_DossierHasComputedAmount_DispatchesWithThatAmount()
    {
        const decimal expectedAmount = 1500.00m;
        const string expectedIban = "MD24AG000225100013104168";

        var harness = Harness.Create();
        var seeded = await harness.SeedDispatchableAsync(
            closeDossier: true,
            computedAmountMdl: expectedAmount,
            bankIban: expectedIban);

        MPayOutbound? captured = null;
        harness.MPay
            .SendAsync(Arg.Do<MPayOutbound>(o => captured = o), Arg.Any<CancellationToken>())
            .Returns(Result<MPayReceipt>.Success(new MPayReceipt("TX-CAPTURED", "ACCEPTED")));

        await harness.Job.Execute(FakeContext());

        captured.Should().NotBeNull();
        captured!.AmountMdl.Should().Be(expectedAmount);
        captured.BeneficiaryIban.Should().Be(expectedIban);

        // Sanity: the row was stamped so a subsequent run would skip it.
        var app = await harness.Db.Applications.SingleAsync(a => a.Id == seeded.AppId);
        app.PaymentDispatchedAtUtc.Should().Be(ClockNow);
    }

    // ─────────────────────── Test plumbing ───────────────────────

    private static IJobExecutionContext FakeContext()
    {
        var ctx = Substitute.For<IJobExecutionContext>();
        ctx.CancellationToken.Returns(CancellationToken.None);
        return ctx;
    }

    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }

    private sealed record SeedResult(
        long AppId,
        string? AppReference,
        string NationalId,
        string Iban,
        decimal Amount,
        string DossierNumber);

    private sealed class Harness
    {
        public required CnasDbContext Db { get; init; }
        public required MPayDispatcherJob Job { get; init; }
        public required IMPayClient MPay { get; init; }
        public required IAuditService Audit { get; init; }
        public required ISqidService Sqids { get; init; }

        public static Harness Create()
        {
            var db = CreateContext();
            var clock = new StubClock(ClockNow);
            var mpay = Substitute.For<IMPayClient>();
            var audit = Substitute.For<IAuditService>();
            audit.RecordAsync(
                    Arg.Any<string>(), Arg.Any<AuditSeverity>(), Arg.Any<string>(),
                    Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
                    Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success()));
            var sqids = Substitute.For<ISqidService>();
            sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");

            var job = new MPayDispatcherJob(
                db, mpay, clock, audit, NullLogger<MPayDispatcherJob>.Instance, sqids);
            return new Harness
            {
                Db = db,
                Job = job,
                MPay = mpay,
                Audit = audit,
                Sqids = sqids,
            };
        }

        public async Task<SeedResult> SeedDispatchableAsync(
            bool closeDossier,
            decimal? computedAmountMdl = null,
            string? bankIban = null)
        {
            var solicitant = new Solicitant
            {
                CreatedAtUtc = ClockNow,
                NationalId = "2000000000007",
                Kind = ApplicantKind.NaturalPerson,
                DisplayName = "Ion Popescu",
                PreferredLanguage = "ro",
                BankIban = bankIban ?? "MD24AG000225100013104168",
                IsActive = true,
            };
            Db.Solicitants.Add(solicitant);

            var passport = new ServicePassport
            {
                CreatedAtUtc = ClockNow,
                Code = "SP-TEST",
                NameRo = "Test",
                DescriptionRo = "Test",
                FormSchemaJson = "{}",
                WorkflowCode = "WF-TEST",
                MaxProcessingDays = 30,
                IsEnabled = true,
                IsActive = true,
            };
            Db.ServicePassports.Add(passport);
            await Db.SaveChangesAsync();

            var app = new ServiceApplication
            {
                CreatedAtUtc = ClockNow,
                SolicitantId = solicitant.Id,
                ServicePassportId = passport.Id,
                Status = ApplicationStatus.Approved,
                FormPayloadJson = "{}",
                ReferenceNumber = "PS-TEST-0001",
                IsActive = true,
            };
            Db.Applications.Add(app);
            await Db.SaveChangesAsync();

            var dossier = new Dossier
            {
                CreatedAtUtc = ClockNow,
                ApplicationId = app.Id,
                DossierNumber = "D-2026-PAY00001",
                ComputedAmountMdl = computedAmountMdl ?? 11000.50m,
                ClosedAtUtc = closeDossier ? ClockNow.AddMinutes(-5) : null,
                IsActive = true,
            };
            Db.Dossiers.Add(dossier);
            await Db.SaveChangesAsync();

            return new SeedResult(
                app.Id,
                app.ReferenceNumber,
                solicitant.NationalId,
                solicitant.BankIban!,
                dossier.ComputedAmountMdl!.Value,
                dossier.DossierNumber);
        }
    }
}
