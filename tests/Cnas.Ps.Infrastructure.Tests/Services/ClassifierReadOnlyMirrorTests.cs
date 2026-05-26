using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Classifiers;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Persistence.Seed;
using Cnas.Ps.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// R0401 / TOR CF 17.02-04 — pins the read-only-mirror contract for the five
/// official national classifier schemes (CAEM Rev.2, CUATM, CFOJ, CFP, NCM)
/// seeded by <see cref="NationalClassifiersSeed"/>:
/// <list type="bullet">
///   <item>each national scheme is represented by at least one stub row;</item>
///   <item>every seeded row carries <c>IsReadOnlyMirror=true</c>;</item>
///   <item><c>IClassifierService.UpsertAsync</c> rejects edits with
///         <c>CLASSIFIER.READONLY_MIRROR</c>;</item>
///   <item><c>IClassifierService.DeactivateAsync</c> rejects soft-deletes with
///         <c>CLASSIFIER.READONLY_MIRROR</c>;</item>
///   <item><see cref="ClassifierSchemeFamilies.LookupFamily"/> reports
///         <see cref="ClassifierSchemeFamily.National"/> for every seeded
///         scheme.</item>
/// </list>
/// Plus a regression assertion that internal (non-mirror) rows still mutate
/// successfully — the read-only gate must not bleed onto everything else.
/// </summary>
public sealed class ClassifierReadOnlyMirrorTests
{
    /// <summary>Deterministic clock instant used across the suite.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 24, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Stub clock returning a fixed UTC instant. The architecture test
    /// <c>TimeProviderUsageTests</c> forbids <see cref="DateTime.UtcNow"/>
    /// anywhere outside the system clock implementation, so every callsite
    /// goes through this contract.
    /// </summary>
    /// <param name="now">The deterministic UTC instant to surface.</param>
    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        /// <inheritdoc />
        public DateTime UtcNow { get; } = now;
    }

    /// <summary>
    /// All five seeded schemes carry at least one row + every row is flagged
    /// <c>IsReadOnlyMirror=true</c>. This guards against accidental deletion
    /// of a scheme from <see cref="NationalClassifiersSeed.BuildAll"/>.
    /// </summary>
    [Fact]
    public async Task BuildAll_EveryNationalSchemePresentAndFlaggedReadOnly()
    {
        var db = CreateContext();
        var rows = NationalClassifiersSeed.BuildAll(ClockNow);
        db.Classifiers.AddRange(rows);
        await db.SaveChangesAsync();

        foreach (var scheme in ClassifierSchemeFamilies.NationalSchemes)
        {
            var schemeRows = await db.Classifiers.Where(c => c.Kind == scheme).ToListAsync();
            schemeRows.Should().NotBeEmpty($"scheme {scheme} must seed at least one stub row");
            schemeRows.Should().OnlyContain(r => r.IsReadOnlyMirror,
                $"every seeded {scheme} row must be flagged as a read-only mirror");
            schemeRows.Should().OnlyContain(r => r.Source == "national",
                $"every seeded {scheme} row must carry Source=national");
        }
    }

    /// <summary>
    /// Upserting against an existing national-mirror row must fail with the
    /// stable code <see cref="ErrorCodes.ClassifierReadonlyMirror"/>. The
    /// underlying row's labels must remain unchanged so the local mirror stays
    /// in lock-step with the authoritative source.
    /// </summary>
    [Fact]
    public async Task UpsertAsync_OnNationalMirror_ReturnsReadOnlyMirrorError()
    {
        var db = CreateContext();
        var rows = NationalClassifiersSeed.BuildCaemSample(ClockNow);
        db.Classifiers.AddRange(rows);
        await db.SaveChangesAsync();

        var service = NewService(db);
        var target = rows[0];
        var tampered = new ClassifierRow(
            Kind: target.Kind,
            Code: target.Code,
            LabelRo: "Tampered label",
            LabelEn: target.LabelEn,
            LabelRu: target.LabelRu,
            ParentCode: target.ParentCode,
            Source: target.Source);

        var result = await service.UpsertAsync(tampered);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ClassifierReadonlyMirror);

        // Reload and assert the original row was NOT mutated.
        var reloaded = await db.Classifiers.SingleAsync(c => c.Kind == target.Kind && c.Code == target.Code);
        reloaded.LabelRo.Should().Be(target.LabelRo,
            "the read-only-mirror gate must reject mutations rather than partially-apply them");
    }

    /// <summary>
    /// Deactivating a national-mirror row must fail with the stable code
    /// <see cref="ErrorCodes.ClassifierReadonlyMirror"/>; the underlying row
    /// must remain active so the local mirror stays in sync.
    /// </summary>
    [Fact]
    public async Task DeactivateAsync_OnNationalMirror_ReturnsReadOnlyMirrorError()
    {
        var db = CreateContext();
        var rows = NationalClassifiersSeed.BuildCfpSample(ClockNow);
        db.Classifiers.AddRange(rows);
        await db.SaveChangesAsync();

        var service = NewService(db);
        var target = rows[0]; // SRL

        var result = await service.DeactivateAsync(target.Kind, target.Code);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ClassifierReadonlyMirror);

        var reloaded = await db.Classifiers.SingleAsync(c => c.Kind == target.Kind && c.Code == target.Code);
        reloaded.IsActive.Should().BeTrue("national-mirror rows cannot be soft-deleted locally");
    }

    /// <summary>
    /// Every national-scheme code must map to <see cref="ClassifierSchemeFamily.National"/>
    /// through <see cref="ClassifierSchemeFamilies.LookupFamily"/>.
    /// </summary>
    [Fact]
    public void LookupFamily_ForEveryNationalScheme_ReturnsNational()
    {
        foreach (var scheme in ClassifierSchemeFamilies.NationalSchemes)
        {
            ClassifierSchemeFamilies.LookupFamily(scheme).Should().Be(
                ClassifierSchemeFamily.National,
                $"scheme {scheme} is in the official national bucket");
        }

        ClassifierSchemeFamilies.LookupFamily("DECISION_TYPE").Should().Be(
            ClassifierSchemeFamily.Internal,
            "schemes not on the national list fall through to the internal family");
        ClassifierSchemeFamilies.LookupFamily(null).Should().Be(
            ClassifierSchemeFamily.Internal,
            "null scheme codes degrade safely to internal");
    }

    /// <summary>
    /// Regression — internal (non-mirror) classifier rows still upsert + soft-
    /// delete successfully. The read-only-mirror gate must not bleed onto the
    /// non-national bucket; without this pin a future refactor could silently
    /// freeze every classifier mutation.
    /// </summary>
    [Fact]
    public async Task UpsertAndDeactivate_OnInternalRow_StillSucceed()
    {
        var db = CreateContext();
        db.Classifiers.Add(new Classifier
        {
            CreatedAtUtc = ClockNow,
            Kind = "DECISION_TYPE",
            Code = "APPROVED",
            LabelRo = "Aprobată",
            Source = "internal",
            IsActive = true,
            IsReadOnlyMirror = false,
        });
        await db.SaveChangesAsync();

        var service = NewService(db);

        // Upsert: relabel the internal row.
        var upsert = await service.UpsertAsync(new ClassifierRow(
            Kind: "DECISION_TYPE",
            Code: "APPROVED",
            LabelRo: "Aprobată (revizuită)",
            LabelEn: "Approved",
            LabelRu: "Одобрено",
            ParentCode: null,
            Source: "internal"));
        upsert.IsSuccess.Should().BeTrue("internal rows remain mutable");

        var reloaded = await db.Classifiers.SingleAsync(c => c.Kind == "DECISION_TYPE" && c.Code == "APPROVED");
        reloaded.LabelRo.Should().Be("Aprobată (revizuită)");

        // Deactivate: should succeed (no references in this isolated harness).
        var deactivate = await service.DeactivateAsync("DECISION_TYPE", "APPROVED");
        deactivate.IsSuccess.Should().BeTrue("internal rows still soft-delete when not referenced");
        var afterDeactivate = await db.Classifiers.SingleAsync(c => c.Kind == "DECISION_TYPE" && c.Code == "APPROVED");
        afterDeactivate.IsActive.Should().BeFalse();
    }

    /// <summary>
    /// Builds a service wired against the supplied context with a stub clock
    /// and a guard that always reports zero references. The
    /// <c>IClassifierReferenceGuard</c> contract is exercised by
    /// <c>ClassifierServiceTests</c> / <c>ClassifierReferenceGuardTests</c>;
    /// this fixture short-circuits it to keep the read-only-mirror gate
    /// isolated.
    /// </summary>
    /// <param name="db">The EF Core context to inject.</param>
    /// <returns>A wired <see cref="IClassifierService"/>.</returns>
    private static IClassifierService NewService(CnasDbContext db)
    {
        var guard = Substitute.For<IClassifierReferenceGuard>();
        guard.ScanAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => Result<ClassifierReferenceScanResultDto>.Success(
                new ClassifierReferenceScanResultDto(
                    SchemeCode: ci.ArgAt<string>(0),
                    Value: ci.ArgAt<string>(1),
                    ReferencingRowCount: 0,
                    ReferencingEntities: Array.Empty<ClassifierReferencingEntityDto>())));
        return new ClassifierService(db, new StubClock(ClockNow), guard);
    }

    /// <summary>Creates a fresh EF Core InMemory context with a unique database name.</summary>
    /// <returns>A converter-less context — encrypted columns persist as plaintext.</returns>
    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-classifier-readonly-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }
}
