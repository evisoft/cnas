using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// Unit tests for <see cref="WorkflowConfigurationService"/> — UC16 workflow-definition
/// repository. Validates the versioned, append-only persistence contract:
/// <list type="bullet">
///   <item>first <c>SaveDefinitionAsync</c> for a code inserts version 1 with <c>IsCurrent=true</c>;</item>
///   <item>subsequent saves increment the version and flip the previous row's <c>IsCurrent</c> to <c>false</c>;</item>
///   <item><c>GetDefinitionAsync</c> returns the row whose <c>IsCurrent=true</c>;</item>
///   <item>code canonicalization is case-insensitive (workflow codes are uppercase by convention);</item>
///   <item>invalid / empty JSON payloads are rejected with <see cref="ErrorCodes.ValidationFailed"/>;</item>
///   <item>unknown codes return <see cref="ErrorCodes.NotFound"/>.</item>
/// </list>
/// </summary>
/// <remarks>
/// Each test builds a fresh EF Core InMemory <see cref="CnasDbContext"/> with a unique
/// database name so the suite is parallel-safe. The <see cref="ICnasTimeProvider"/>
/// collaborator is a hand-rolled stub returning a fixed UTC instant for determinism.
/// Per CLAUDE.md RULE 1 these tests are written BEFORE the production implementation
/// flips from its sentinel-failure stub to the real versioned repository.
/// </remarks>
public class WorkflowConfigurationServiceTests
{
    /// <summary>Deterministic clock instant used by every test in this suite.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 20, 12, 0, 0, DateTimeKind.Utc);

    // ─────────────────────── SaveDefinitionAsync ───────────────────────

    [Fact]
    public async Task SaveDefinitionAsync_NewCode_InsertsVersionOne_WithIsCurrent()
    {
        // Arrange — fresh harness with no workflow rows.
        await using var harness = Harness.Create();

        // Act
        var result = await harness.Service.SaveDefinitionAsync(
            "WF-NEW", "{\"states\":[\"submitted\"]}", CancellationToken.None);

        // Assert — success and a single row with Version=1, IsCurrent=true.
        result.IsSuccess.Should().BeTrue();
        var row = await harness.Db.WorkflowDefinitions.SingleAsync();
        row.Code.Should().Be("WF-NEW");
        row.Version.Should().Be(1);
        row.IsCurrent.Should().BeTrue();
        row.DefinitionJson.Should().Be("{\"states\":[\"submitted\"]}");
        row.CreatedAtUtc.Should().Be(ClockNow);
    }

    [Fact]
    public async Task SaveDefinitionAsync_ExistingCode_IncrementsVersionAndDeactivatesPrevious()
    {
        // Arrange — first version already persisted.
        await using var harness = Harness.Create();
        var first = await harness.Service.SaveDefinitionAsync(
            "WF-EXIST", "{\"v\":1}", CancellationToken.None);
        first.IsSuccess.Should().BeTrue();

        // Act — second save for the same code.
        var second = await harness.Service.SaveDefinitionAsync(
            "WF-EXIST", "{\"v\":2}", CancellationToken.None);

        // Assert — two rows; the older has IsCurrent=false, the newer Version=2 IsCurrent=true.
        second.IsSuccess.Should().BeTrue();
        var rows = await harness.Db.WorkflowDefinitions
            .Where(r => r.Code == "WF-EXIST")
            .OrderBy(r => r.Version)
            .ToListAsync();
        rows.Should().HaveCount(2);
        rows[0].Version.Should().Be(1);
        rows[0].IsCurrent.Should().BeFalse();
        rows[0].DefinitionJson.Should().Be("{\"v\":1}");
        rows[1].Version.Should().Be(2);
        rows[1].IsCurrent.Should().BeTrue();
        rows[1].DefinitionJson.Should().Be("{\"v\":2}");
    }

    [Fact]
    public async Task SaveDefinitionAsync_InvalidJson_ReturnsValidationFailed()
    {
        // Arrange
        await using var harness = Harness.Create();

        // Act — payload is not parseable as JSON.
        var result = await harness.Service.SaveDefinitionAsync(
            "WF-BAD-JSON", "{ this is not valid json", CancellationToken.None);

        // Assert — Result.Failure(VALIDATION_FAILED), no row persisted.
        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
        (await harness.Db.WorkflowDefinitions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task SaveDefinitionAsync_EmptyDefinition_ReturnsValidationFailed()
    {
        // Arrange
        await using var harness = Harness.Create();

        // Act — whitespace-only body is rejected (cannot be parsed as a valid JSON document).
        var result = await harness.Service.SaveDefinitionAsync(
            "WF-EMPTY", "   ", CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
        (await harness.Db.WorkflowDefinitions.CountAsync()).Should().Be(0);
    }

    // ─────────────────────── GetDefinitionAsync ───────────────────────

    [Fact]
    public async Task GetDefinitionAsync_KnownCode_ReturnsLatestJson()
    {
        // Arrange — two versions saved; GET must return the latest (IsCurrent=true).
        await using var harness = Harness.Create();
        await harness.Service.SaveDefinitionAsync("WF-GET", "{\"v\":1}", CancellationToken.None);
        await harness.Service.SaveDefinitionAsync("WF-GET", "{\"v\":2}", CancellationToken.None);

        // Act
        var result = await harness.Service.GetDefinitionAsync("WF-GET", CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("{\"v\":2}");
    }

    [Fact]
    public async Task GetDefinitionAsync_UnknownCode_ReturnsNotFoundResult()
    {
        // Arrange — no rows.
        await using var harness = Harness.Create();

        // Act
        var result = await harness.Service.GetDefinitionAsync("WF-MISSING", CancellationToken.None);

        // Assert — NOT_FOUND so the controller maps to 404 (not 400).
        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }

    [Fact]
    public async Task GetDefinitionAsync_CodeCaseInsensitive_FindsSameRow()
    {
        // Arrange — the service canonicalises to upper-case before persistence + lookup,
        // so saving as "wf-case" must round-trip via either "WF-CASE" or "wf-case" on GET.
        await using var harness = Harness.Create();
        await harness.Service.SaveDefinitionAsync(
            "wf-case", "{\"steps\":[]}", CancellationToken.None);

        // Act
        var upper = await harness.Service.GetDefinitionAsync("WF-CASE", CancellationToken.None);
        var lower = await harness.Service.GetDefinitionAsync("wf-case", CancellationToken.None);
        var mixed = await harness.Service.GetDefinitionAsync("Wf-CaSe", CancellationToken.None);

        // Assert — all three queries hit the same row.
        upper.IsSuccess.Should().BeTrue();
        lower.IsSuccess.Should().BeTrue();
        mixed.IsSuccess.Should().BeTrue();
        upper.Value.Should().Be("{\"steps\":[]}");
        lower.Value.Should().Be("{\"steps\":[]}");
        mixed.Value.Should().Be("{\"steps\":[]}");

        // And the canonical persisted form is upper-case so external tooling reading the
        // table directly sees a consistent identifier.
        var row = await harness.Db.WorkflowDefinitions.SingleAsync();
        row.Code.Should().Be("WF-CASE");
    }

    // ─────────────────────── Test harness ───────────────────────

    /// <summary>Creates a fresh EF Core InMemory context with a unique database name.</summary>
    /// <returns>A converter-less context — none of the workflow columns are encrypted.</returns>
    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-workflow-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    /// <summary>Stub clock returning a fixed UTC instant for deterministic timestamps.</summary>
    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        /// <inheritdoc />
        public DateTime UtcNow { get; } = now;
    }

    /// <summary>Stub caller — supplies a single deterministic Sqid id for audit-row actor.</summary>
    private sealed class StubCaller : ICallerContext
    {
        /// <inheritdoc />
        public long? UserId => 1;
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
    /// Black-hole stub for <see cref="IAuditService"/> — the legacy tests in this class
    /// do not assert audit behaviour. Audit-shape assertions live in
    /// <see cref="DefinitionVersioningTests"/>.
    /// </summary>
    private sealed class NoOpAudit : IAuditService
    {
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
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());
    }

    /// <summary>
    /// Bundles the SUT and its EF context so tests stay focused on the assertions rather
    /// than wiring boilerplate. The harness implements <see cref="IAsyncDisposable"/> so
    /// the InMemory database is released between tests.
    /// </summary>
    private sealed class Harness : IAsyncDisposable
    {
        /// <summary>EF Core context wrapping the InMemory database.</summary>
        public required CnasDbContext Db { get; init; }

        /// <summary>System under test.</summary>
        public required WorkflowConfigurationService Service { get; init; }

        /// <summary>Builds a fresh harness with a new InMemory database.</summary>
        public static Harness Create()
        {
            var db = CreateContext();
            var clock = new StubClock(ClockNow);
            var caller = new StubCaller();
            var audit = new NoOpAudit();
            var service = new WorkflowConfigurationService(db, clock, caller, audit);
            return new Harness { Db = db, Service = service };
        }

        /// <summary>Releases the underlying InMemory database.</summary>
        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync().ConfigureAwait(false);
        }
    }
}
