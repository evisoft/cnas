using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Contributors;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Common;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// R0311 / ARH 028 / TOR Annex 2.3 — change-traceability of InsuredPerson child rows.
/// Tests cover civil-status supersession, social-insurance contract validation,
/// pre-1999 Carnet de muncă inserts, and the audit-event shape.
/// </summary>
public class ContributorLinkedEntitiesServiceTests
{
    private static readonly DateTime ClockNow = new(2026, 5, 22, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task UpdateCivilStatus_FromSingleToMarried_SupersedesAndRecordsAudit()
    {
        await using var h = await Harness.CreateAsync();
        var contributorId = h.SeedContributor();
        await h.Service.UpdateCivilStatusAsync(contributorId,
            new ContributorCivilStatusInputDto("Single", null), null, CancellationToken.None);
        h.Audit.Events.Clear();

        await h.Service.UpdateCivilStatusAsync(contributorId,
            new ContributorCivilStatusInputDto("Married", new DateOnly(2025, 1, 15)),
            "marriage-cert", CancellationToken.None);

        var rows = await h.Db.ContributorCivilStatuses
            .Where(c => c.ContributorId == contributorId).OrderBy(c => c.ValidFromUtc).ToListAsync();
        rows.Should().HaveCount(2);
        rows[0].Status.Should().Be(CivilStatusType.Single);
        rows[0].ValidToUtc.Should().Be(ClockNow);
        rows[1].Status.Should().Be(CivilStatusType.Married);
        rows[1].ValidToUtc.Should().BeNull();
        h.Audit.Events.Should().Contain(e => e.EventCode == "CONTRIBUTORCIVILSTATUS.UPDATED");
    }

    [Fact]
    public async Task UpdateSocialInsuranceContract_EndBeforeStart_RejectsAtServiceBoundary()
    {
        await using var h = await Harness.CreateAsync();
        var contributorId = h.SeedContributor();
        var input = new ContributorSocialInsuranceContractInputDto(
            ContractNumber: "C-001",
            ContractStartDate: new DateOnly(2026, 5, 1),
            ContractEndDate: new DateOnly(2026, 4, 1),
            MonthlyContributionAmount: 100m,
            CounterpartyName: null);

        var res = await h.Service.UpdateSocialInsuranceContractAsync(
            contributorId, input, null, CancellationToken.None);

        res.IsFailure.Should().BeTrue();
        res.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task AddPre1999Period_ValidPeriod_PersistsAndListsBack()
    {
        await using var h = await Harness.CreateAsync();
        var contributorId = h.SeedContributor();

        var add = await h.Service.AddPre1999PeriodAsync(contributorId,
            new ContributorPre1999PeriodCarnetMuncaInputDto(
                CarnetMuncaNumber: "CM-1234567",
                PeriodStartDate: new DateOnly(1985, 1, 1),
                PeriodEndDate: new DateOnly(1989, 12, 31),
                EmployerName: "Uzina Tractoare",
                Position: "Inginer"),
            "digitized", CancellationToken.None);

        add.IsSuccess.Should().BeTrue();
        var list = await h.Service.ListPre1999PeriodsAsync(contributorId, CancellationToken.None);
        list.IsSuccess.Should().BeTrue();
        list.Value.Should().ContainSingle(p => p.CarnetMuncaNumber == "CM-1234567"
            && p.EmployerName == "Uzina Tractoare");
    }

    // ─── Helpers ────────────────────────────────────────────

    private sealed class StubClock : ICnasTimeProvider
    {
        private DateTime _now;
        public StubClock(DateTime now) { _now = now; }
        public DateTime UtcNow => _now;
        public void Advance(TimeSpan span) { _now = _now + span; }
    }

    private sealed class RecordingAudit : IAuditService
    {
        public List<(string EventCode, AuditSeverity Severity, string DetailsJson)> Events { get; } = new();
        public Task<Result> RecordAsync(string eventCode, AuditSeverity severity, string actorId,
            string? targetEntity, long? targetEntityId, string detailsJson, string? sourceIp,
            string? correlationId, CancellationToken cancellationToken = default)
        {
            Events.Add((eventCode, severity, detailsJson));
            return Task.FromResult(Result.Success());
        }
    }

    private sealed class StubCaller : ICallerContext
    {
        public long? UserId => 1;
        public string? UserSqid => "caller-sqid";
        public IReadOnlyCollection<string> Roles { get; } = new[] { "CnasAdmin" };
        public string? SourceIp => "127.0.0.1";
        public string? CorrelationId => "corr-1";
        public string? OnBehalfOfPrincipalIdnp => null;
        public string? DelegationPowerId => null;
        public IAccessScope AccessScope => RolesBasedAccessScope.Unscoped;
        public string? SessionId => null;
    }

    private sealed class Harness : IAsyncDisposable
    {
        public required CnasDbContext Db { get; init; }
        public required ContributorLinkedEntitiesService Service { get; init; }
        public required ISqidService Sqids { get; init; }
        public required RecordingAudit Audit { get; init; }
        public required StubClock Clock { get; init; }

        public static Task<Harness> CreateAsync()
        {
            var opts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-contributor-linked-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var db = new CnasDbContext(opts);
            var clock = new StubClock(ClockNow);
            var sqids = new SqidService(Options.Create(new SqidOptions
            {
                Alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789",
                MinLength = 6,
            }));
            var audit = new RecordingAudit();
            var caller = new StubCaller();
            var service = new ContributorLinkedEntitiesService(db, clock, sqids, caller, audit);
            return Task.FromResult(new Harness
            {
                Db = db,
                Service = service,
                Sqids = sqids,
                Audit = audit,
                Clock = clock,
            });
        }

        public long SeedContributor()
        {
            var c = new InsuredPerson
            {
                Idnp = "1003600012346",
                IdnpHash = "hash-seed",
                FirstName = "Ana",
                LastName = "Popescu",
                BirthDate = new DateOnly(1990, 5, 10),
                RegisteredAtUtc = ClockNow.AddDays(-30),
                CreatedAtUtc = ClockNow.AddDays(-30),
                IsActive = true,
            };
            Db.InsuredPersons.Add(c);
            Db.SaveChanges();
            return c.Id;
        }

        public async ValueTask DisposeAsync() => await Db.DisposeAsync().ConfigureAwait(false);
    }
}
