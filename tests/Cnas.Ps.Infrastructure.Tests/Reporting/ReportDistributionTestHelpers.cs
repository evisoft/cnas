using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Reporting;

/// <summary>
/// R1906 / TOR Annex 6 — shared helpers for the report-distribution test
/// suite. Provides the EF Core InMemory factory + the stub clock used
/// across the validator / service / dispatcher tests.
/// </summary>
internal static class ReportDistributionTestHelpers
{
    /// <summary>Canonical "now" used across the test suite.</summary>
    public static readonly DateTime ClockNow = new(2026, 5, 23, 3, 0, 0, DateTimeKind.Utc);

    /// <summary>Creates a fresh EF Core InMemory <see cref="CnasDbContext"/>.</summary>
    /// <returns>A new context backed by a uniquely named InMemory store.</returns>
    public static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-report-dist-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    /// <summary>
    /// Builds an <see cref="ISqidService"/> mock with the canonical
    /// <c>SQID-{id}</c> encoding shared by the rest of the test suite.
    /// </summary>
    /// <returns>The mock instance.</returns>
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

    /// <summary>Test-only clock returning the canonical instant.</summary>
    public sealed class StubClock : ICnasTimeProvider
    {
        /// <summary>Constructs the clock with a fixed UTC instant.</summary>
        /// <param name="now">Instant returned from <see cref="UtcNow"/>.</param>
        public StubClock(DateTime now) { UtcNow = now; }

        /// <inheritdoc />
        public DateTime UtcNow { get; }
    }

    /// <summary>
    /// Builds an <see cref="IAuditService"/> mock that captures every audit
    /// code passed to <see cref="IAuditService.RecordAsync"/>.
    /// </summary>
    /// <param name="capturedCodes">Out-parameter receiving the populated list.</param>
    /// <returns>The mock instance.</returns>
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

    /// <summary>Builds an <see cref="ICallerContext"/> mock with sensible defaults.</summary>
    /// <returns>The mock instance.</returns>
    public static ICallerContext NewCaller()
    {
        var caller = Substitute.For<ICallerContext>();
        caller.UserId.Returns(1L);
        caller.UserSqid.Returns("SQID-1");
        caller.SourceIp.Returns("203.0.113.7");
        caller.CorrelationId.Returns("corr-report-dist");
        return caller;
    }

    /// <summary>Builds a no-op <see cref="IDeterministicHasher"/> that echoes the input.</summary>
    /// <returns>The mock instance.</returns>
    public static IDeterministicHasher NewHasher()
    {
        var hasher = Substitute.For<IDeterministicHasher>();
        hasher.ComputeHash(Arg.Any<string>())
            .Returns(call => "HASH:" + (call.Arg<string>() ?? string.Empty).Trim().ToUpperInvariant());
        return hasher;
    }
}
