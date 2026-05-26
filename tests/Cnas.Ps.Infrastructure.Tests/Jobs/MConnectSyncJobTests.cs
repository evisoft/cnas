using Cnas.Ps.Application.Abstractions;
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
/// Unit tests for <see cref="MConnectSyncJob"/>. The job refreshes
/// <see cref="InsuredPerson"/> rows whose <c>LastRspSyncUtc</c> is null or older than
/// 30 days from the RSP registry via MConnect. Failures are tolerated — the row stays
/// untouched and will be retried on the next daily run.
/// </summary>
public class MConnectSyncJobTests
{
    private static readonly DateTime ClockNow = new(2026, 5, 19, 3, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Execute_StalePerson_CallsMConnectAndUpdatesFields()
    {
        var harness = Harness.Create();
        var person = await harness.SeedPersonAsync(lastSyncUtc: null);

        // Successful RSP response — fresh family name, deceased flag flipped.
        harness.MConnect
            .CallAsync("RSP.GetPerson", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<string>.Success(
                """{"lastName":"Popescu","firstName":"Maria","patronymic":"Ionovna","isDeceased":true,"dateOfDeath":"2026-05-01"}"""));

        await harness.Job.Execute(FakeContext());

        var reloaded = await harness.Db.InsuredPersons.SingleAsync(p => p.Id == person.Id);
        reloaded.LastName.Should().Be("Popescu");
        reloaded.FirstName.Should().Be("Maria");
        reloaded.Patronymic.Should().Be("Ionovna");
        reloaded.IsDeceased.Should().BeTrue();
        reloaded.DateOfDeath.Should().Be(new DateOnly(2026, 5, 1));
        reloaded.LastRspSyncUtc.Should().Be(ClockNow);
    }

    [Fact]
    public async Task Execute_MConnectFailure_LeavesRecordUntouched()
    {
        var harness = Harness.Create();
        var person = await harness.SeedPersonAsync(lastSyncUtc: null);
        var originalName = person.LastName;

        harness.MConnect
            .CallAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<string>.Failure(ErrorCodes.MConnectFailed, "upstream 500"));

        await harness.Job.Execute(FakeContext());

        var reloaded = await harness.Db.InsuredPersons.SingleAsync(p => p.Id == person.Id);
        reloaded.LastName.Should().Be(originalName);
        reloaded.LastRspSyncUtc.Should().BeNull();
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

    private sealed class Harness
    {
        public required CnasDbContext Db { get; init; }
        public required MConnectSyncJob Job { get; init; }
        public required IMConnectClient MConnect { get; init; }

        public static Harness Create()
        {
            var db = CreateContext();
            var clock = new StubClock(ClockNow);
            var mconnect = Substitute.For<IMConnectClient>();
            var job = new MConnectSyncJob(db, mconnect, clock, NullLogger<MConnectSyncJob>.Instance);
            return new Harness { Db = db, Job = job, MConnect = mconnect };
        }

        public async Task<InsuredPerson> SeedPersonAsync(DateTime? lastSyncUtc)
        {
            var person = new InsuredPerson
            {
                CreatedAtUtc = ClockNow,
                Idnp = "2000000000007",
                LastName = "Ionescu",
                FirstName = "Vasile",
                BirthDate = new DateOnly(1980, 6, 15),
                RegisteredAtUtc = ClockNow.AddYears(-5),
                IsDeceased = false,
                LastRspSyncUtc = lastSyncUtc,
                IsActive = true,
            };
            Db.InsuredPersons.Add(person);
            await Db.SaveChangesAsync();
            return person;
        }
    }
}
