using Cnas.Ps.Application.Classifiers;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// R0403 / TOR CF 17.08 — unit tests pinning the active-only invariant for
/// <see cref="ClassifierService.ListAsync"/>. Selection-list dropdowns in the
/// Blazor UI bind to this method through the
/// <c>IClassifierLookup</c> facade; the contract is that callers see
/// <b>only</b> rows with <c>IsActive == true</c> so deprecated codes never
/// resurface in a citizen-facing or examiner-facing pick list.
/// </summary>
/// <remarks>
/// Per CLAUDE.md RULE 1 this test was authored to pin the existing service
/// behaviour against regression before the new <c>IClassifierLookup</c> and
/// the <see cref="Cnas.Ps.Web.Components.ClassifierPicker"/> /
/// <c>ClassifierMultiPicker</c> Blazor components were wired up.
/// </remarks>
public sealed class ClassifierServiceTests
{
    /// <summary>Deterministic clock instant used across the suite.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 24, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Active codes the seed exposes — hoisted to a static array to satisfy CA1861.</summary>
    private static readonly string[] ExpectedActiveCodes = ["01.11", "02.10"];

    /// <summary>
    /// Seeds a scheme with a mix of active + deactivated rows, then asserts
    /// only the active ones surface from <see cref="ClassifierService.ListAsync"/>.
    /// The contract holds the line between the catalogue (everything ever
    /// scheme-coded) and the pick list (what a user may still select today).
    /// </summary>
    [Fact]
    public async Task ListAsync_OnlyReturnsActiveRows()
    {
        var db = CreateContext();

        db.Classifiers.AddRange(
            new Classifier
            {
                CreatedAtUtc = ClockNow.AddDays(-30),
                Kind = "CAEM",
                Code = "01.11",
                LabelRo = "Cultivare cereale",
                Source = "national",
                IsActive = true,
            },
            new Classifier
            {
                CreatedAtUtc = ClockNow.AddDays(-30),
                Kind = "CAEM",
                Code = "02.10",
                LabelRo = "Silvicultură",
                Source = "national",
                IsActive = true,
            },
            new Classifier
            {
                CreatedAtUtc = ClockNow.AddDays(-30),
                Kind = "CAEM",
                Code = "99.99",
                LabelRo = "Cod retras",
                Source = "national",
                IsActive = false, // deactivated — must NOT surface
            });
        await db.SaveChangesAsync();

        var guard = Substitute.For<IClassifierReferenceGuard>();
        var clock = Substitute.For<ICnasTimeProvider>();
        clock.UtcNow.Returns(ClockNow);
        IClassifierService service = new ClassifierService(db, clock, guard);

        var result = await service.ListAsync("CAEM");

        result.IsSuccess.Should().BeTrue();
        result.Value!.Should().HaveCount(2,
            "the deactivated 99.99 row must be filtered out of pick lists per CF 17.08.");
        result.Value.Select(r => r.Code).Should().BeEquivalentTo(ExpectedActiveCodes);
        result.Value.Should().NotContain(r => r.Code == "99.99",
            "deactivated rows must never surface in a selection list (active-only invariant).");
    }

    /// <summary>Creates a fresh EF Core InMemory context with a unique database name.</summary>
    /// <returns>A converter-less context — encrypted columns persist as plaintext.</returns>
    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-classifier-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }
}
