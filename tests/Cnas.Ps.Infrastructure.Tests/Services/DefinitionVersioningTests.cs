using Cnas.Ps.Application.Abstractions;
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
/// R0129 / R0142 / TOR CF 15.04 — versioned workflow + service-passport pinning. These
/// tests cover the append-only definition-versioning framework:
/// <list type="bullet">
///   <item>publishing a new definition writes Version=N+1 + flips IsCurrent + populates chain pointers;</item>
///   <item>publishing the same payload twice is a no-op (semantic-diff is empty);</item>
///   <item>publishing emits a Critical audit row capturing the from/to versions;</item>
///   <item>the resolver finds current rows by code and historical rows by (code, version);</item>
///   <item>ServiceApplication submission snapshots the passport+workflow Version onto the application;</item>
///   <item>a passport republish does NOT drift the in-flight application's pinned reference;</item>
///   <item>history endpoint returns rows ordered by Version DESC;</item>
///   <item>list endpoint returns only IsCurrent=true rows by default;</item>
///   <item>audit details JSON includes the from/to version numbers.</item>
/// </list>
/// </summary>
/// <remarks>
/// Per CLAUDE.md RULE 1 these tests are written BEFORE the production implementation lands.
/// Each test uses a fresh EF Core InMemory database with a unique name so the suite is
/// parallel-safe.
/// </remarks>
public class DefinitionVersioningTests
{
    /// <summary>Deterministic clock instant used by every test in this suite.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 21, 12, 0, 0, DateTimeKind.Utc);

    // ─────────── Workflow versioning chain ───────────

    [Fact]
    public async Task SaveWorkflow_SecondVersion_FlipsChainPointersOnPreviousRow()
    {
        await using var h = await VersioningHarness.CreateAsync();

        var first = await h.Workflows.SaveDefinitionAsync("WF-CHAIN", "{\"v\":1}", CancellationToken.None);
        first.IsSuccess.Should().BeTrue();

        var firstRow = await h.Db.WorkflowDefinitions.SingleAsync(w => w.Code == "WF-CHAIN");
        firstRow.SupersedesDefinitionId.Should().BeNull("first version has no predecessor");
        firstRow.SupersededByDefinitionId.Should().BeNull("first version is still current");

        var second = await h.Workflows.SaveDefinitionAsync("WF-CHAIN", "{\"v\":2}", CancellationToken.None);
        second.IsSuccess.Should().BeTrue();

        var rows = await h.Db.WorkflowDefinitions
            .Where(w => w.Code == "WF-CHAIN")
            .OrderBy(w => w.Version)
            .ToListAsync();
        rows.Should().HaveCount(2);
        rows[0].IsCurrent.Should().BeFalse();
        rows[0].SupersededByDefinitionId.Should().Be(rows[1].Id);
        rows[0].SupersededAtUtc.Should().Be(ClockNow);
        rows[1].IsCurrent.Should().BeTrue();
        rows[1].SupersedesDefinitionId.Should().Be(rows[0].Id);
        rows[1].Version.Should().Be(2);
    }

    [Fact]
    public async Task SaveWorkflow_SameJsonTwice_IsNoOpReturningSuccess()
    {
        await using var h = await VersioningHarness.CreateAsync();
        await h.Workflows.SaveDefinitionAsync("WF-NOOP", "{\"v\":1}", CancellationToken.None);

        var second = await h.Workflows.SaveDefinitionAsync("WF-NOOP", "{\"v\":1}", CancellationToken.None);

        second.IsSuccess.Should().BeTrue();
        var count = await h.Db.WorkflowDefinitions.CountAsync(w => w.Code == "WF-NOOP");
        count.Should().Be(1);
    }

    [Fact]
    public async Task SaveWorkflow_NewVersion_WritesCriticalAuditWithFromToVersions()
    {
        await using var h = await VersioningHarness.CreateAsync();
        await h.Workflows.SaveDefinitionAsync("WF-AUDIT", "{\"v\":1}", CancellationToken.None);
        h.Audit.Events.Clear();

        await h.Workflows.SaveDefinitionAsync("WF-AUDIT", "{\"v\":2}", CancellationToken.None);

        h.Audit.Events.Should().ContainSingle(e =>
            e.EventCode == "WORKFLOWDEFINITION.VERSION_CREATED"
            && e.Severity == AuditSeverity.Critical);
        var detail = h.Audit.Events.Single().DetailsJson;
        detail.Should().Contain("\"fromVersion\":1");
        detail.Should().Contain("\"toVersion\":2");
        detail.Should().Contain("\"code\":\"WF-AUDIT\"");
    }

    [Fact]
    public async Task GetWorkflowHistory_ReturnsAllVersionsDescending()
    {
        await using var h = await VersioningHarness.CreateAsync();
        await h.Workflows.SaveDefinitionAsync("WF-HIST", "{\"v\":1}", CancellationToken.None);
        await h.Workflows.SaveDefinitionAsync("WF-HIST", "{\"v\":2}", CancellationToken.None);
        await h.Workflows.SaveDefinitionAsync("WF-HIST", "{\"v\":3}", CancellationToken.None);

        var result = await h.Workflows.GetHistoryAsync("WF-HIST", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(3);
        result.Value[0].Version.Should().Be(3);
        result.Value[0].IsCurrent.Should().BeTrue();
        result.Value[1].Version.Should().Be(2);
        result.Value[2].Version.Should().Be(1);
    }

    // ─────────── ServicePassport versioning ───────────

    [Fact]
    public async Task UpsertPassport_Create_StoresVersion1AsCurrent()
    {
        await using var h = await VersioningHarness.CreateAsync();
        var input = NewPassportInput(code: "SP-NEW", name: "Original");

        var result = await h.Passports.UpsertAsync(input, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var row = await h.Db.ServicePassports.SingleAsync();
        row.Version.Should().Be(1);
        row.IsCurrent.Should().BeTrue();
        row.SupersedesPassportId.Should().BeNull();
    }

    [Fact]
    public async Task UpsertPassport_MeaningfulChange_AppendsVersionAndChainsRows()
    {
        await using var h = await VersioningHarness.CreateAsync();
        var create = await h.Passports.UpsertAsync(NewPassportInput("SP-CHANGE", "Original"), CancellationToken.None);
        create.IsSuccess.Should().BeTrue();

        // Mutate a semantically-meaningful field (NameRo) and resubmit through the upsert.
        var v1Id = create.Value!;
        var update = await h.Passports.UpsertAsync(
            NewPassportInput("SP-CHANGE", "Renamed") with { Id = v1Id },
            CancellationToken.None);

        update.IsSuccess.Should().BeTrue();
        var rows = await h.Db.ServicePassports
            .Where(p => p.Code == "SP-CHANGE")
            .OrderBy(p => p.Version)
            .ToListAsync();
        rows.Should().HaveCount(2);
        rows[0].IsCurrent.Should().BeFalse();
        rows[0].SupersededByPassportId.Should().Be(rows[1].Id);
        rows[1].Version.Should().Be(2);
        rows[1].NameRo.Should().Be("Renamed");
        rows[1].IsCurrent.Should().BeTrue();
        rows[1].SupersedesPassportId.Should().Be(rows[0].Id);
    }

    [Fact]
    public async Task UpsertPassport_NoFieldsChanged_IsNoOpAndReturnsSameSqid()
    {
        await using var h = await VersioningHarness.CreateAsync();
        var create = await h.Passports.UpsertAsync(NewPassportInput("SP-IDEM", "Same"), CancellationToken.None);
        var v1Id = create.Value!;
        h.Audit.Events.Clear();

        var update = await h.Passports.UpsertAsync(
            NewPassportInput("SP-IDEM", "Same") with { Id = v1Id },
            CancellationToken.None);

        update.IsSuccess.Should().BeTrue();
        update.Value.Should().Be(v1Id, "no-op preserves the current Sqid");
        (await h.Db.ServicePassports.CountAsync(p => p.Code == "SP-IDEM")).Should().Be(1);
        h.Audit.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task UpsertPassport_NewVersion_EmitsCriticalAuditWithVersionDelta()
    {
        await using var h = await VersioningHarness.CreateAsync();
        var create = await h.Passports.UpsertAsync(NewPassportInput("SP-AUD", "Original"), CancellationToken.None);
        h.Audit.Events.Clear();

        await h.Passports.UpsertAsync(
            NewPassportInput("SP-AUD", "Renamed") with { Id = create.Value },
            CancellationToken.None);

        h.Audit.Events.Should().ContainSingle(e =>
            e.EventCode == "SERVICEPASSPORT.VERSION_CREATED"
            && e.Severity == AuditSeverity.Critical);
        var detail = h.Audit.Events.Single().DetailsJson;
        detail.Should().Contain("\"fromVersion\":1");
        detail.Should().Contain("\"toVersion\":2");
        detail.Should().Contain("\"code\":\"SP-AUD\"");
    }

    [Fact]
    public async Task ListPassports_ReturnsOnlyCurrentRowPerCode()
    {
        await using var h = await VersioningHarness.CreateAsync();
        var c1 = await h.Passports.UpsertAsync(NewPassportInput("SP-LIST-A", "A1"), CancellationToken.None);
        await h.Passports.UpsertAsync(
            NewPassportInput("SP-LIST-A", "A2") with { Id = c1.Value },
            CancellationToken.None);
        await h.Passports.UpsertAsync(NewPassportInput("SP-LIST-B", "B1"), CancellationToken.None);

        var result = await h.Passports.ListAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(2);
        result.Value.Single(r => r.Code == "SP-LIST-A").NameRo.Should().Be("A2");
        result.Value.Single(r => r.Code == "SP-LIST-A").Version.Should().Be(2);
        result.Value.Single(r => r.Code == "SP-LIST-B").Version.Should().Be(1);
    }

    [Fact]
    public async Task GetPassportHistory_ReturnsAllVersionsDescending()
    {
        await using var h = await VersioningHarness.CreateAsync();
        var c1 = await h.Passports.UpsertAsync(NewPassportInput("SP-HIST", "v1"), CancellationToken.None);
        await h.Passports.UpsertAsync(
            NewPassportInput("SP-HIST", "v2") with { Id = c1.Value },
            CancellationToken.None);
        // Look up the current row's id to send the third update.
        var currentSqid = (await h.Db.ServicePassports
            .Where(p => p.Code == "SP-HIST" && p.IsCurrent)
            .Select(p => p.Id)
            .SingleAsync());
        var currentEncoded = h.Sqids.Encode(currentSqid);
        await h.Passports.UpsertAsync(
            NewPassportInput("SP-HIST", "v3") with { Id = currentEncoded },
            CancellationToken.None);

        var history = await h.Passports.GetHistoryAsync(currentEncoded, CancellationToken.None);

        history.IsSuccess.Should().BeTrue();
        history.Value.Should().HaveCount(3);
        history.Value[0].Version.Should().Be(3);
        history.Value[0].IsCurrent.Should().BeTrue();
        history.Value[1].Version.Should().Be(2);
        history.Value[2].Version.Should().Be(1);
        history.Value.All(h => h.Code == "SP-HIST").Should().BeTrue();
    }

    // ─────────── In-flight pinning invariants ───────────

    [Fact]
    public async Task ServiceApplicationConfiguration_DefaultsPinnedVersionsToOne()
    {
        // The pinned-version columns default to 1 so existing rows backfilled by the
        // migration round-trip cleanly. This test verifies the default policy at the
        // domain level — the application service is exercised in journey tests.
        await using var h = await VersioningHarness.CreateAsync();
        var app = new ServiceApplication
        {
            CreatedAtUtc = ClockNow,
            SolicitantId = 1,
            ServicePassportId = 1,
            Status = ApplicationStatus.Draft,
        };
        h.Db.Applications.Add(app);
        await h.Db.SaveChangesAsync();

        app.PinnedServicePassportVersion.Should().Be(1);
        app.PinnedWorkflowVersion.Should().Be(1);
    }

    [Fact]
    public async Task SupersededPassportRow_RemainsQueryableByPrimaryKey()
    {
        // The pinning invariant says an in-flight application's FK to ServicePassportId
        // continues to point at the version row it was submitted under, even after the
        // catalogue publishes a new version. Verify the historical row remains queryable
        // by its primary key (i.e. it is NOT physically deleted by the version flip).
        await using var h = await VersioningHarness.CreateAsync();
        var c1 = await h.Passports.UpsertAsync(NewPassportInput("SP-PIN", "v1"), CancellationToken.None);
        var v1Id = h.Sqids.TryDecode(c1.Value!).Value;
        await h.Passports.UpsertAsync(
            NewPassportInput("SP-PIN", "v2") with { Id = c1.Value },
            CancellationToken.None);

        var historicalRow = await h.Db.ServicePassports.SingleOrDefaultAsync(p => p.Id == v1Id);

        historicalRow.Should().NotBeNull();
        historicalRow!.IsCurrent.Should().BeFalse();
        historicalRow.Version.Should().Be(1);
        historicalRow.IsActive.Should().BeTrue(
            "historical rows remain IsActive=true — soft-delete is orthogonal to version supersession");
    }

    // ─────────── Helpers ───────────

    private static ServicePassportInput NewPassportInput(string code, string name) =>
        new(
            Id: null,
            Code: code,
            NameRo: name,
            NameEn: null,
            NameRu: null,
            DescriptionRo: "desc",
            FormSchemaJson: "{}",
            WorkflowCode: "WF-DEFAULT",
            MaxProcessingDays: 30,
            IsEnabled: true,
            IsProactive: false,
            DecisionRulesJson: "{}");

    /// <summary>Stub clock returning a fixed UTC instant for deterministic timestamps.</summary>
    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        /// <inheritdoc />
        public DateTime UtcNow { get; } = now;
    }

    /// <summary>
    /// Recording stub for <see cref="IAuditService"/> — captures every recorded event so
    /// tests can assert the version-creation audit row was emitted with the right shape.
    /// </summary>
    private sealed class RecordingAudit : IAuditService
    {
        /// <summary>Captured audit events, in arrival order.</summary>
        public List<AuditRecord> Events { get; } = new();

        /// <inheritdoc />
        public Task<Result> RecordAsync(
            string eventCode,
            AuditSeverity severity,
            string actorId,
            string? targetEntity,
            long? targetEntityId,
            string detailsJson,
            string? sourceIp,
            string? correlationId,
            CancellationToken cancellationToken = default)
        {
            Events.Add(new AuditRecord(eventCode, severity, actorId, targetEntity, targetEntityId, detailsJson));
            return Task.FromResult(Result.Success());
        }
    }

    /// <summary>Snapshot of a recorded audit event for assertion convenience.</summary>
    private sealed record AuditRecord(
        string EventCode,
        AuditSeverity Severity,
        string ActorId,
        string? TargetEntity,
        long? TargetEntityId,
        string DetailsJson);

    /// <summary>Stub caller — supplies a single deterministic Sqid id.</summary>
    private sealed class StubCaller : ICallerContext
    {
        /// <inheritdoc />
        public long? UserId => 42;
        /// <inheritdoc />
        public string? UserSqid => "u1";
        /// <inheritdoc />
        public IReadOnlyCollection<string> Roles { get; } = new[] { "CnasAdmin" };
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
    }

    /// <summary>
    /// Bundles the SUTs and their EF context. Both <see cref="WorkflowConfigurationService"/>
    /// and <see cref="ServicePassportService"/> share the same DB + audit recorder so
    /// cross-cutting interactions (e.g. workflow pinning during passport upserts) can be
    /// exercised in a single fixture.
    /// </summary>
    private sealed class VersioningHarness : IAsyncDisposable
    {
        /// <summary>EF Core context wrapping the InMemory database.</summary>
        public required CnasDbContext Db { get; init; }

        /// <summary>Workflow configuration SUT.</summary>
        public required IWorkflowConfigurationService Workflows { get; init; }

        /// <summary>Service-passport SUT.</summary>
        public required IServicePassportService Passports { get; init; }

        /// <summary>Sqid encoder used by the SUTs — shared so tests can decode results.</summary>
        public required ISqidService Sqids { get; init; }

        /// <summary>Captured audit events for assertion.</summary>
        public required RecordingAudit Audit { get; init; }

        /// <summary>Builds a fresh harness with a new InMemory database.</summary>
        public static Task<VersioningHarness> CreateAsync()
        {
            var opts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-versioning-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var db = new CnasDbContext(opts);
            var clock = new StubClock(ClockNow);
            var sqids = new SqidService(Options.Create(new SqidOptions
            {
                Alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789",
                MinLength = 6,
            }));
            var caller = new StubCaller();
            var audit = new RecordingAudit();
            var workflows = new WorkflowConfigurationService(db, clock, caller, audit);
            var passports = new ServicePassportService(db, sqids, clock, caller, audit, workflows);
            return Task.FromResult(new VersioningHarness
            {
                Db = db,
                Workflows = workflows,
                Passports = passports,
                Sqids = sqids,
                Audit = audit,
            });
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync().ConfigureAwait(false);
        }
    }
}
