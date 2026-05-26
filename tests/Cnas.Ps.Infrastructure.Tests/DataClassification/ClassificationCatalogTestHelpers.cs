using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.DataClassification;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.DataClassification;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.DataClassification;

/// <summary>
/// R2279 / TOR SEC 033 — shared helpers for the classification-catalog test
/// suite. Provides the EF Core InMemory factory + the stub clock + the
/// Sqid / audit / caller test doubles used by service + job + controller tests.
/// </summary>
internal static class ClassificationCatalogTestHelpers
{
    /// <summary>Canonical "now" used across the classification tests.</summary>
    public static readonly DateTime ClockNow = new(2026, 5, 23, 3, 30, 0, DateTimeKind.Utc);

    /// <summary>Creates a fresh EF Core InMemory <see cref="CnasDbContext"/>.</summary>
    /// <returns>A new context backed by a uniquely named InMemory store.</returns>
    public static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-classification-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    /// <summary>Test-only clock returning the canonical instant.</summary>
    public sealed class StubClock : ICnasTimeProvider
    {
        /// <summary>Constructs the clock with a fixed UTC instant.</summary>
        /// <param name="now">Instant returned from <see cref="UtcNow"/>.</param>
        public StubClock(DateTime now) { UtcNow = now; }

        /// <inheritdoc />
        public DateTime UtcNow { get; }
    }

    /// <summary>Builds an <see cref="ISqidService"/> substitute that maps <c>SQID-{id}</c> ↔ <c>id</c>.</summary>
    /// <returns>Configured substitute.</returns>
    public static ISqidService NewSqidMock()
    {
        var sqids = Substitute.For<ISqidService>();
        sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");
        sqids.TryDecode(Arg.Any<string>()).Returns(call =>
        {
            var s = call.Arg<string>();
            if (s is not null && s.StartsWith("SQID-", StringComparison.Ordinal)
                && long.TryParse(s["SQID-".Length..], out var id))
            {
                return Result<long>.Success(id);
            }
            return Result<long>.Failure(ErrorCodes.InvalidSqid, "bad sqid");
        });
        return sqids;
    }

    /// <summary>
    /// Builds an <see cref="IAuditService"/> substitute that captures the
    /// emitted event codes into <paramref name="capturedCodes"/>.
    /// </summary>
    /// <param name="capturedCodes">Out-list populated by every Record call.</param>
    /// <returns>Configured substitute.</returns>
    public static IAuditService NewAudit(out List<string> capturedCodes)
    {
        var codes = new List<string>();
        capturedCodes = codes;
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
                codes.Add(call.ArgAt<string>(0));
                return Task.FromResult(Result.Success());
            });
        return audit;
    }

    /// <summary>Builds an <see cref="ICallerContext"/> substitute for an admin caller.</summary>
    /// <returns>Configured substitute.</returns>
    public static ICallerContext NewCaller()
    {
        var caller = Substitute.For<ICallerContext>();
        caller.UserId.Returns(7L);
        caller.UserSqid.Returns("USR-7");
        caller.SourceIp.Returns("203.0.113.7");
        caller.CorrelationId.Returns("corr-classification");
        return caller;
    }

    /// <summary>
    /// Builds an <see cref="IClassificationCatalogScanner"/> stub that returns
    /// the supplied properties + label counts.
    /// </summary>
    /// <param name="properties">Properties the stub returns.</param>
    /// <param name="totalTypesScanned">Counter override.</param>
    /// <param name="assemblyVersions">Optional assembly-version map.</param>
    /// <returns>Configured stub.</returns>
    public static IClassificationCatalogScanner NewStubScanner(
        IReadOnlyList<ScannedPropertyDto> properties,
        int totalTypesScanned = 1,
        IReadOnlyDictionary<string, string>? assemblyVersions = null)
    {
        var classified = 0;
        var unclassified = 0;
        var labelCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var p in properties)
        {
            if (p.IsExplicit) { classified++; } else { unclassified++; }
            labelCounts.TryGetValue(p.Label, out var existing);
            labelCounts[p.Label] = existing + 1;
        }
        assemblyVersions ??= new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Cnas.Ps.Contracts"] = "1.0.0.0",
        };

        var outcome = new ClassificationCatalogScanOutcomeDto(
            TotalTypesScanned: totalTypesScanned,
            TotalPropertiesClassified: classified,
            TotalPropertiesUnclassified: unclassified,
            Properties: properties,
            LabelCounts: labelCounts,
            AssemblyVersions: assemblyVersions);

        var scanner = Substitute.For<IClassificationCatalogScanner>();
        scanner.ScanAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<ClassificationCatalogScanOutcomeDto>.Success(outcome)));
        return scanner;
    }

    /// <summary>Builds the service under test with sensible defaults.</summary>
    /// <param name="db">Writer context.</param>
    /// <param name="scanner">Scanner stub.</param>
    /// <param name="audit">Audit substitute.</param>
    /// <param name="caller">Caller substitute (default: <see cref="NewCaller"/>).</param>
    /// <returns>The configured service.</returns>
    public static ClassificationCatalogService NewService(
        CnasDbContext db,
        IClassificationCatalogScanner scanner,
        IAuditService audit,
        ICallerContext? caller = null)
        => new(
            db: db,
            scanner: scanner,
            audit: audit,
            sqids: NewSqidMock(),
            clock: new StubClock(ClockNow),
            caller: caller ?? NewCaller(),
            entryFilterValidator: new ClassificationCatalogEntryFilterValidator(),
            driftFilterValidator: new ClassificationDriftFilterValidator(),
            ackValidator: new ClassificationDriftAcknowledgeInputValidator());
}
