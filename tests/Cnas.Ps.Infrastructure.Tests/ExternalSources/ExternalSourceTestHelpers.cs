using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.ExternalSources;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.ExternalSources;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.ExternalSources;

/// <summary>
/// R0203 / TOR CF 20.06 — shared helpers for the external-source ingestion
/// test suite. Mirrors the pattern of <c>TreasuryFeedTestHelpers</c>.
/// </summary>
internal static class ExternalSourceTestHelpers
{
    /// <summary>Canonical "now" used across the tests (2026-05-24 02:00 UTC).</summary>
    public static readonly DateTime ClockNow = new(2026, 5, 24, 2, 0, 0, DateTimeKind.Utc);

    /// <summary>Test-only fixed UTC clock.</summary>
    public sealed class StubClock : ICnasTimeProvider
    {
        /// <summary>Constructs the clock.</summary>
        /// <param name="now">Instant returned from <see cref="UtcNow"/>.</param>
        public StubClock(DateTime now) { UtcNow = now; }

        /// <inheritdoc />
        public DateTime UtcNow { get; }
    }

    /// <summary>Builds a fresh EF Core InMemory context backed by a unique store.</summary>
    /// <returns>A new context.</returns>
    public static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-external-sources-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    /// <summary>Returns a Sqid mock that round-trips "SQID-{id}".</summary>
    /// <returns>Configured mock.</returns>
    public static ISqidService NewSqidMock()
    {
        var s = Substitute.For<ISqidService>();
        s.Encode(Arg.Any<long>()).Returns(c => $"SQID-{c.Arg<long>()}");
        s.TryDecode(Arg.Any<string>()).Returns(c =>
        {
            var v = c.Arg<string>();
            if (v is not null && v.StartsWith("SQID-", StringComparison.Ordinal)
                && long.TryParse(v["SQID-".Length..], out var id))
            {
                return Result<long>.Success(id);
            }
            return Result<long>.Failure(ErrorCodes.InvalidSqid, "bad sqid");
        });
        return s;
    }

    /// <summary>Audit mock capturing every event code + severity tuple written.</summary>
    /// <param name="entries">Out parameter — list of (event, severity) tuples.</param>
    /// <returns>Configured mock.</returns>
    public static IAuditService NewAuditCapturing(out List<(string Code, AuditSeverity Severity)> entries)
    {
        var list = new List<(string, AuditSeverity)>();
        entries = list;
        var a = Substitute.For<IAuditService>();
        a.RecordAsync(
                Arg.Any<string>(), Arg.Any<AuditSeverity>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(c =>
            {
                list.Add((c.ArgAt<string>(0), c.ArgAt<AuditSeverity>(1)));
                return Task.FromResult(Result.Success());
            });
        return a;
    }

    /// <summary>Caller-context mock returning sqid USR-1.</summary>
    /// <returns>Configured mock.</returns>
    public static ICallerContext NewCaller()
    {
        var c = Substitute.For<ICallerContext>();
        c.UserId.Returns(1L);
        c.UserSqid.Returns("USR-1");
        c.SourceIp.Returns("203.0.113.7");
        c.CorrelationId.Returns("corr-ext");
        return c;
    }

    /// <summary>Builds the ingestion service with sensible defaults.</summary>
    /// <param name="db">Context (used as both writer + reader for InMemory tests).</param>
    /// <param name="audit">Audit service.</param>
    /// <param name="connectors">Connectors to register; empty by default.</param>
    /// <param name="fallback">In-memory fallback connector instance.</param>
    /// <returns>Configured service.</returns>
    public static ExternalSourceIngestionService NewService(
        CnasDbContext db,
        IAuditService audit,
        IEnumerable<IExternalSourceConnector>? connectors = null,
        InMemoryExternalSourceConnector? fallback = null)
        => new(
            db: db,
            read: db,
            clock: new StubClock(ClockNow),
            sqids: NewSqidMock(),
            caller: NewCaller(),
            audit: audit,
            connectors: connectors ?? Array.Empty<IExternalSourceConnector>(),
            fallback: fallback ?? new InMemoryExternalSourceConnector(),
            triggerValidator: new ExternalSourceManualTriggerInputValidator(),
            filterValidator: new ExternalSourceIngestionRunFilterValidator(),
            logger: NullLogger<ExternalSourceIngestionService>.Instance);
}
