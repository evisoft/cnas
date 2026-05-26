using System.Diagnostics.Metrics;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.AccessScope;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.AccessScope;
using Cnas.Ps.Infrastructure.Observability;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Qbe;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.AccessScope;

/// <summary>
/// R0671 continuation — service-level tests for
/// <see cref="AccessScopeBackfillService"/>. Wires the real QBE converter +
/// access-scope filter against an InMemory DB and substitute audit + sqid +
/// caller + clock collaborators.
/// </summary>
public sealed class AccessScopeBackfillServiceTests
{
    /// <summary>UTC instant used for every clock substitute in this fixture.</summary>
    private static readonly DateTime BaseUtc = new(2026, 5, 23, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>Fresh InMemory <see cref="CnasDbContext"/> with a unique database name.</summary>
    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-accessscope-backfill-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    /// <summary>Builds the SUT around the supplied DB + collaborator substitutes.</summary>
    private static (AccessScopeBackfillService Svc, CnasDbContext Db, IAuditService Audit)
        Build(CnasDbContext db, Dictionary<string, long>? sqidMap = null)
    {
        var sqids = Substitute.For<ISqidService>();
        sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");
        sqids.TryDecode(Arg.Any<string?>()).Returns(call =>
        {
            var s = call.Arg<string?>();
            if (string.IsNullOrEmpty(s))
            {
                return Result<long>.Failure(ErrorCodes.InvalidSqid, "empty");
            }
            if (sqidMap is not null && sqidMap.TryGetValue(s, out var mapped))
            {
                return Result<long>.Success(mapped);
            }
            // Fall back: parse leading-S prefix used by the local Encode stub.
            if (s.StartsWith("SQID-", StringComparison.Ordinal)
                && long.TryParse(s.AsSpan(5), out var id))
            {
                return Result<long>.Success(id);
            }
            return Result<long>.Failure(ErrorCodes.InvalidSqid, $"unknown sqid {s}");
        });

        var clock = Substitute.For<ICnasTimeProvider>();
        clock.UtcNow.Returns(BaseUtc);

        var audit = Substitute.For<IAuditService>();
        audit.RecordAsync(
                Arg.Any<string>(), Arg.Any<AuditSeverity>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));

        var caller = Substitute.For<ICallerContext>();
        caller.UserSqid.Returns("SQID-actor");
        caller.SourceIp.Returns("127.0.0.1");
        caller.CorrelationId.Returns("corr-1");

        var qbeConverter = new QbeToLinqConverter(new QbeRegistrySchemaProvider());

        var svc = new AccessScopeBackfillService(db, clock, sqids, audit, qbeConverter, caller);
        return (svc, db, audit);
    }

    /// <summary>Seeds three solicitants with mixed region codes.</summary>
    private static async Task SeedSolicitantsAsync(CnasDbContext db)
    {
        db.Solicitants.AddRange(
            new Solicitant { Id = 1, NationalId = "1", NationalIdHash = "h1", DisplayName = "Alpha", RegionCode = null },
            new Solicitant { Id = 2, NationalId = "2", NationalIdHash = "h2", DisplayName = "Beta", RegionCode = null },
            new Solicitant { Id = 3, NationalId = "3", NationalIdHash = "h3", DisplayName = "Gamma", RegionCode = "BLT" });
        await db.SaveChangesAsync();
    }

    /// <summary>Seeds a default active CnasBranch so the SubdivisionCode validation passes.</summary>
    private static async Task SeedBranchAsync(CnasDbContext db, string code = "CHISINAU-CENTRU")
    {
        db.CnasBranches.Add(new CnasBranch
        {
            Code = code,
            Name = "Test Branch",
            City = "Chișinău",
            IsActive = true,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Seeds three service applications with mixed subdivision codes.</summary>
    private static async Task SeedApplicationsAsync(CnasDbContext db)
    {
        db.Applications.AddRange(
            new ServiceApplication
            {
                Id = 1, SolicitantId = 1, ServicePassportId = 1, FormPayloadJson = "{}",
                Status = ApplicationStatus.Approved, SubdivisionCode = null,
            },
            new ServiceApplication
            {
                Id = 2, SolicitantId = 2, ServicePassportId = 1, FormPayloadJson = "{}",
                Status = ApplicationStatus.Rejected, SubdivisionCode = null,
            },
            new ServiceApplication
            {
                Id = 3, SolicitantId = 3, ServicePassportId = 1, FormPayloadJson = "{}",
                Status = ApplicationStatus.Approved, SubdivisionCode = "BALTI",
            });
        await db.SaveChangesAsync();
    }

    // ─────────────────────── AssignSolicitantRegionByPatternAsync ───────────────────────

    /// <summary>
    /// Explicit Sqids only — only the rows whose ids appear on the list are
    /// updated; others keep their NULL RegionCode.
    /// </summary>
    [Fact]
    public async Task AssignSolicitantRegion_ExplicitSqids_UpdatesOnlyMatchingRows()
    {
        await using var db = CreateContext();
        await SeedSolicitantsAsync(db);
        var (svc, _, _) = Build(db);

        var input = new AccessScopeSolicitantBackfillInputDto(
            RegionCode: "CHIS",
            ExplicitSolicitantSqids: ["SQID-1", "SQID-2"]);

        var result = await svc.AssignSolicitantRegionByPatternAsync(input);

        result.IsSuccess.Should().BeTrue();
        result.Value.RowsUpdated.Should().Be(2);
        result.Value.MatchedSqidCount.Should().Be(2);
        result.Value.Failures.Should().BeEmpty();

        var rows = await db.Solicitants.OrderBy(s => s.Id).ToListAsync();
        rows[0].RegionCode.Should().Be("CHIS");
        rows[1].RegionCode.Should().Be("CHIS");
        rows[2].RegionCode.Should().Be("BLT");
    }

    /// <summary>
    /// QBE filter only — every row matching the predicate is updated.
    /// </summary>
    [Fact]
    public async Task AssignSolicitantRegion_QbeFilter_UpdatesFilteredSet()
    {
        await using var db = CreateContext();
        await SeedSolicitantsAsync(db);
        var (svc, _, _) = Build(db);

        // Filter on Id <= 2 — should match rows 1 and 2.
        var qbe = new QbeFilterDto("AND", new[]
        {
            new QbeConditionDto("Id", "LessOrEqual", "2"),
        });
        var input = new AccessScopeSolicitantBackfillInputDto(
            RegionCode: "CHIS",
            Filter: qbe);

        var result = await svc.AssignSolicitantRegionByPatternAsync(input);

        result.IsSuccess.Should().BeTrue();
        result.Value.RowsUpdated.Should().Be(2);
        result.Value.MatchedSqidCount.Should().Be(0);
    }

    /// <summary>
    /// Both Filter AND ExplicitSolicitantSqids — the row sets are unioned so a
    /// row matching either selection is updated.
    /// </summary>
    [Fact]
    public async Task AssignSolicitantRegion_QbeAndExplicit_UnionsTheSelections()
    {
        await using var db = CreateContext();
        await SeedSolicitantsAsync(db);
        var (svc, _, _) = Build(db);

        // QBE filter matches row 3 (Id = 3); explicit list matches row 1.
        // Union = {1, 3}; row 2 keeps its NULL.
        var qbe = new QbeFilterDto("AND", new[]
        {
            new QbeConditionDto("Id", "Equals", "3"),
        });
        var input = new AccessScopeSolicitantBackfillInputDto(
            RegionCode: "CHIS",
            Filter: qbe,
            ExplicitSolicitantSqids: ["SQID-1"]);

        var result = await svc.AssignSolicitantRegionByPatternAsync(input);

        result.IsSuccess.Should().BeTrue();
        result.Value.RowsUpdated.Should().Be(2);

        var row2 = await db.Solicitants.SingleAsync(s => s.Id == 2);
        row2.RegionCode.Should().BeNull();
    }

    /// <summary>
    /// A Sqid that decodes successfully but references a row that does not exist
    /// surfaces as a per-row failure with <see cref="ErrorCodes.InvalidId"/>.
    /// </summary>
    [Fact]
    public async Task AssignSolicitantRegion_UnknownSqid_RecordsAsFailure()
    {
        await using var db = CreateContext();
        await SeedSolicitantsAsync(db);
        var (svc, _, _) = Build(db);

        var input = new AccessScopeSolicitantBackfillInputDto(
            RegionCode: "CHIS",
            ExplicitSolicitantSqids: ["SQID-1", "SQID-999"]);

        var result = await svc.AssignSolicitantRegionByPatternAsync(input);

        result.IsSuccess.Should().BeTrue();
        result.Value.RowsUpdated.Should().Be(1);
        result.Value.MatchedSqidCount.Should().Be(1);
        result.Value.Failures.Should().ContainSingle(f =>
            f.ErrorCode == ErrorCodes.InvalidId);
    }

    /// <summary>
    /// Resolved row count above the 5000-cap → Validation failure carrying
    /// the <see cref="ErrorCodes.BackfillQuotaExceeded"/> code.
    /// </summary>
    [Fact]
    public async Task AssignSolicitantRegion_OverCap_ReturnsBackfillQuotaExceeded()
    {
        await using var db = CreateContext();
        // Seed 5001 solicitants (the +1 puts us above the cap).
        for (int i = 1; i <= AccessScopeBackfillService.MaxRowsPerCall + 1; i++)
        {
            db.Solicitants.Add(new Solicitant
            {
                Id = i,
                NationalId = i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                NationalIdHash = $"h{i}",
                DisplayName = $"S{i}",
                RegionCode = null,
            });
        }
        await db.SaveChangesAsync();
        var (svc, _, _) = Build(db);

        // Match-all filter so every row is in the resolved set.
        var qbe = new QbeFilterDto("AND", new[]
        {
            new QbeConditionDto("Id", "GreaterThan", "0"),
        });
        var input = new AccessScopeSolicitantBackfillInputDto(
            RegionCode: "CHIS",
            Filter: qbe);

        var result = await svc.AssignSolicitantRegionByPatternAsync(input);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.BackfillQuotaExceeded);
    }

    /// <summary>
    /// A successful call emits exactly one Critical audit row with the
    /// <c>ACCESS_SCOPE.BACKFILL.SOLICITANT</c> event code.
    /// </summary>
    [Fact]
    public async Task AssignSolicitantRegion_EmitsCriticalAuditSummary()
    {
        await using var db = CreateContext();
        await SeedSolicitantsAsync(db);
        var (svc, _, audit) = Build(db);

        var input = new AccessScopeSolicitantBackfillInputDto(
            RegionCode: "CHIS",
            ExplicitSolicitantSqids: ["SQID-1"]);

        var result = await svc.AssignSolicitantRegionByPatternAsync(input);

        result.IsSuccess.Should().BeTrue();
        await audit.Received(1).RecordAsync(
            AccessScopeBackfillService.SolicitantAuditCode,
            AuditSeverity.Critical,
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<long?>(),
            Arg.Is<string>(json => json.Contains("\"code\":\"CHIS\"", StringComparison.Ordinal)
                && json.Contains("\"rowsUpdated\":1", StringComparison.Ordinal)),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A successful Solicitant back-fill increments the
    /// <c>cnas.access_scope.backfilled</c> counter with the <c>kind=Solicitant</c>
    /// tag. Uses a <see cref="MeterListener"/> harness to capture the measurement.
    /// </summary>
    [Fact]
    public async Task AssignSolicitantRegion_IncrementsAccessScopeBackfilledCounter()
    {
        await using var db = CreateContext();
        await SeedSolicitantsAsync(db);
        var (svc, _, _) = Build(db);

        using var capture = new KindCapture("cnas.access_scope.backfilled");
        var input = new AccessScopeSolicitantBackfillInputDto(
            RegionCode: "CHIS",
            ExplicitSolicitantSqids: ["SQID-1", "SQID-2"]);

        var result = await svc.AssignSolicitantRegionByPatternAsync(input);

        result.IsSuccess.Should().BeTrue();
        capture.Kinds.Should().Contain("Solicitant");
        capture.Total.Should().Be(2);
    }

    // ─────────────────────── AssignServiceApplicationSubdivisionByPatternAsync ───────────────────────

    /// <summary>
    /// Happy path: the subdivision code is an active branch + explicit Sqids
    /// resolve cleanly; the rows are updated and the audit row fires.
    /// </summary>
    [Fact]
    public async Task AssignApplicationSubdivision_KnownBranch_UpdatesRows()
    {
        await using var db = CreateContext();
        await SeedBranchAsync(db);
        await SeedApplicationsAsync(db);
        var (svc, _, _) = Build(db);

        var input = new AccessScopeApplicationBackfillInputDto(
            SubdivisionCode: "CHISINAU-CENTRU",
            ExplicitApplicationSqids: ["SQID-1", "SQID-2"]);

        var result = await svc.AssignServiceApplicationSubdivisionByPatternAsync(input);

        result.IsSuccess.Should().BeTrue();
        result.Value.RowsUpdated.Should().Be(2);
        var rows = await db.Applications.OrderBy(a => a.Id).ToListAsync();
        rows[0].SubdivisionCode.Should().Be("CHISINAU-CENTRU");
        rows[1].SubdivisionCode.Should().Be("CHISINAU-CENTRU");
        rows[2].SubdivisionCode.Should().Be("BALTI");
    }

    /// <summary>
    /// Unknown branch code → Validation failure with
    /// <see cref="ErrorCodes.BranchNotFound"/>. No rows are touched.
    /// </summary>
    [Fact]
    public async Task AssignApplicationSubdivision_UnknownBranch_ReturnsBranchNotFound()
    {
        await using var db = CreateContext();
        await SeedBranchAsync(db);
        await SeedApplicationsAsync(db);
        var (svc, _, _) = Build(db);

        var input = new AccessScopeApplicationBackfillInputDto(
            SubdivisionCode: "GHOST-BRANCH",
            ExplicitApplicationSqids: ["SQID-1"]);

        var result = await svc.AssignServiceApplicationSubdivisionByPatternAsync(input);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.BranchNotFound);

        // No rows touched.
        var rows = await db.Applications.ToListAsync();
        rows.Should().OnlyContain(a => a.SubdivisionCode == null || a.SubdivisionCode == "BALTI");
    }

    // ─────────────────────── Telemetry harness ───────────────────────

    /// <summary>
    /// <see cref="MeterListener"/>-based capture that records the <c>kind</c>
    /// tag from every measurement on the named instrument plus the cumulative
    /// total. Disposes the listener at end-of-test.
    /// </summary>
    private sealed class KindCapture : IDisposable
    {
        private readonly MeterListener _listener;
        private readonly List<string> _kinds = new();
        private long _total;
        private readonly object _gate = new();

        public IReadOnlyList<string> Kinds
        {
            get { lock (_gate) return _kinds.ToList(); }
        }

        public long Total
        {
            get { lock (_gate) return _total; }
        }

        public KindCapture(string instrumentName)
        {
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Name == CnasMeter.MeterName
                        && instrument.Name == instrumentName)
                    {
                        listener.EnableMeasurementEvents(instrument);
                    }
                },
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            {
                lock (_gate)
                {
                    _total += value;
                    foreach (var t in tags)
                    {
                        if (t.Key == "kind" && t.Value is string s)
                        {
                            _kinds.Add(s);
                        }
                    }
                }
            });
            _listener.Start();
        }

        public void Dispose() => _listener.Dispose();
    }
}
