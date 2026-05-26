using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.AccessScope;
using Cnas.Ps.Infrastructure.Common;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// R0362 / TOR UC13 — tests covering the workflow-driven profile-update lifecycle.
/// </summary>
public sealed class ProfileUpdateServiceTests
{
    private static readonly DateTime ClockNow = new(2026, 5, 22, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>Submit creates the parent application AND the child request row in <c>Pending</c>.</summary>
    [Fact]
    public async Task SubmitAsync_CreatesApplicationAndPendingRequest()
    {
        await using var h = await Harness.CreateAsync();
        var contributorId = h.SeedContributor();
        var input = new ProfileUpdateRequestSubmitDto(
            TargetContributorSqid: h.Sqids.Encode(contributorId),
            Type: "Address",
            RequestedChangesJson: BuildAddressJson(),
            Note: "Citizen submitted new address.");

        var res = await h.Service.SubmitAsync(input);

        res.IsSuccess.Should().BeTrue();
        res.Value.Status.Should().Be(nameof(ProfileUpdateRequestStatus.Pending));
        (await h.Db.Applications.CountAsync()).Should().Be(1);
        (await h.Db.ProfileUpdateRequests.CountAsync()).Should().Be(1);
    }

    /// <summary>Approve applies an Address change and flips the row to <c>Applied</c>.</summary>
    [Fact]
    public async Task ApproveAsync_AddressChange_AppliesAndFlipsToApplied()
    {
        await using var h = await Harness.CreateAsync();
        var contributorId = h.SeedContributor();
        var submit = await h.Service.SubmitAsync(new ProfileUpdateRequestSubmitDto(
            TargetContributorSqid: h.Sqids.Encode(contributorId),
            Type: "Address",
            RequestedChangesJson: BuildAddressJson(),
            Note: null));
        var requestId = h.Sqids.TryDecode(submit.Value.Id).Value;

        h.Caller.SetAdmin();
        var approve = await h.Service.ApproveAsync(requestId);

        approve.IsSuccess.Should().BeTrue();
        approve.Value.Status.Should().Be(nameof(ProfileUpdateRequestStatus.Applied));
        (await h.Db.ContributorAddresses.CountAsync(a => a.ContributorId == contributorId))
            .Should().Be(1);
        h.Audit.Events.Should().Contain(e => e.EventCode == "PROFILE.UPDATE.APPLIED"
            && e.Severity == AuditSeverity.Critical);
    }

    /// <summary>Approve with a payload that fails contributor-side validation leaves a <c>Failed</c> row.</summary>
    [Fact]
    public async Task ApproveAsync_InvalidPayload_PersistsFailedRow()
    {
        await using var h = await Harness.CreateAsync();
        var contributorId = h.SeedContributor();
        // Civil-status with an unknown value — fails inside UpdateCivilStatusAsync.
        var submit = await h.Service.SubmitAsync(new ProfileUpdateRequestSubmitDto(
            TargetContributorSqid: h.Sqids.Encode(contributorId),
            Type: "CivilStatus",
            RequestedChangesJson: "{\"status\":\"Cohabiting\",\"effectiveDate\":null}",
            Note: null));
        var requestId = h.Sqids.TryDecode(submit.Value.Id).Value;

        h.Caller.SetAdmin();
        var approve = await h.Service.ApproveAsync(requestId);

        approve.IsFailure.Should().BeTrue();
        var row = await h.Db.ProfileUpdateRequests.SingleAsync();
        row.Status.Should().Be(ProfileUpdateRequestStatus.Failed);
        row.ApplicationErrorJson.Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>Reject flips status, stores the reason, and emits the Notice audit row.</summary>
    [Fact]
    public async Task RejectAsync_FlipsToRejected_AndAudits()
    {
        await using var h = await Harness.CreateAsync();
        var contributorId = h.SeedContributor();
        var submit = await h.Service.SubmitAsync(new ProfileUpdateRequestSubmitDto(
            TargetContributorSqid: h.Sqids.Encode(contributorId),
            Type: "Address",
            RequestedChangesJson: BuildAddressJson(),
            Note: null));
        var requestId = h.Sqids.TryDecode(submit.Value.Id).Value;

        h.Caller.SetAdmin();
        var rej = await h.Service.RejectAsync(requestId, "Outdated proof of residence.");

        rej.IsSuccess.Should().BeTrue();
        var row = await h.Db.ProfileUpdateRequests.SingleAsync();
        row.Status.Should().Be(ProfileUpdateRequestStatus.Rejected);
        row.RejectionReason.Should().Be("Outdated proof of residence.");
        h.Audit.Events.Should().Contain(e => e.EventCode == "PROFILE.UPDATE.REJECTED"
            && e.Severity == AuditSeverity.Notice);
    }

    /// <summary>Approving without the <c>cnas-admin</c> role is rejected with <c>Forbidden</c>.</summary>
    [Fact]
    public async Task ApproveAsync_WithoutAdminRole_IsForbidden()
    {
        await using var h = await Harness.CreateAsync();
        var contributorId = h.SeedContributor();
        var submit = await h.Service.SubmitAsync(new ProfileUpdateRequestSubmitDto(
            TargetContributorSqid: h.Sqids.Encode(contributorId),
            Type: "Address",
            RequestedChangesJson: BuildAddressJson(),
            Note: null));
        var requestId = h.Sqids.TryDecode(submit.Value.Id).Value;

        // Caller defaults to cnas-user — no admin role.
        var approve = await h.Service.ApproveAsync(requestId);

        approve.IsFailure.Should().BeTrue();
        approve.ErrorCode.Should().Be(ErrorCodes.Forbidden);
    }

    // ─── helpers ─────────────────────

    /// <summary>Builds a syntactically-valid Address payload.</summary>
    private static string BuildAddressJson() => JsonSerializer.Serialize(new
    {
        street = "Strada Stefan cel Mare 1",
        city = "Chisinau",
        region = "Chisinau",
        postalCode = "MD2001",
        country = "MD",
    });

    /// <summary>Deterministic clock.</summary>
    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        /// <inheritdoc />
        public DateTime UtcNow { get; } = now;
    }

    /// <summary>Recording in-memory audit sink.</summary>
    private sealed class RecordingAudit : IAuditService
    {
        public List<(string EventCode, AuditSeverity Severity, string DetailsJson)> Events { get; } = new();

        /// <inheritdoc />
        public Task<Result> RecordAsync(string eventCode, AuditSeverity severity, string actorId,
            string? targetEntity, long? targetEntityId, string detailsJson, string? sourceIp,
            string? correlationId, CancellationToken cancellationToken = default)
        {
            Events.Add((eventCode, severity, detailsJson));
            return Task.FromResult(Result.Success());
        }
    }

    /// <summary>Mutable caller context: starts as cnas-user; tests flip to cnas-admin via <see cref="SetAdmin"/>.</summary>
    public sealed class MutableCaller : ICallerContext
    {
        private readonly HashSet<string> _roles = new(StringComparer.OrdinalIgnoreCase) { "cnas-user" };

        /// <inheritdoc />
        public long? UserId => 1;
        /// <inheritdoc />
        public string? UserSqid => "user-sqid";
        /// <inheritdoc />
        public IReadOnlyCollection<string> Roles => _roles;
        /// <inheritdoc />
        public string? SourceIp => "127.0.0.1";
        /// <inheritdoc />
        public string? CorrelationId => "corr-1";
        /// <inheritdoc />
        public string? OnBehalfOfPrincipalIdnp => null;
        /// <inheritdoc />
        public string? DelegationPowerId => null;
        /// <inheritdoc />
        public IAccessScope AccessScope => RolesBasedAccessScope.Unscoped;
        /// <inheritdoc />
        public string? SessionId => null;

        /// <summary>Adds the <c>cnas-admin</c> role to this caller for approve/reject tests.</summary>
        public void SetAdmin() => _roles.Add("cnas-admin");
    }

    /// <summary>Test harness composing an in-memory <see cref="CnasDbContext"/> + the SUT.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        public required CnasDbContext Db { get; init; }
        public required ProfileUpdateService Service { get; init; }
        public required ISqidService Sqids { get; init; }
        public required RecordingAudit Audit { get; init; }
        public required MutableCaller Caller { get; init; }

        public static Task<Harness> CreateAsync()
        {
            var opts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-profile-update-{Guid.NewGuid():N}")
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
            var caller = new MutableCaller();
            var contributorWriter = new ContributorLinkedEntitiesService(db, clock, sqids, caller, audit);
            var service = new ProfileUpdateService(db, contributorWriter, clock, sqids, caller, audit);
            return Task.FromResult(new Harness
            {
                Db = db,
                Service = service,
                Sqids = sqids,
                Audit = audit,
                Caller = caller,
            });
        }

        /// <summary>Seeds one InsuredPerson row and returns its id.</summary>
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

        /// <inheritdoc />
        public async ValueTask DisposeAsync() => await Db.DisposeAsync().ConfigureAwait(false);
    }
}
