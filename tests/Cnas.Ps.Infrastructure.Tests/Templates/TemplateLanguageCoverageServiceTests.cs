using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Templates;

/// <summary>
/// R2003 / R0133 — unit tests for
/// <see cref="TemplateLanguageCoverageService"/>. Covers the pure-read
/// coverage projection, the persisted-finding scan, the deduplication
/// behaviour, retired-template exclusion, OnlyApproved toggle, custom
/// required-language sets, audit emission, and the counters.
/// </summary>
public sealed class TemplateLanguageCoverageServiceTests
{
    private static readonly DateTime ClockNow = new(2026, 5, 23, 3, 45, 0, DateTimeKind.Utc);

    /// <summary>CA1861 — hoisted to a static field to avoid per-call allocation.</summary>
    private static readonly string[] RoRu = ["ro", "ru"];

    /// <summary>CA1861 — single-entry RO override set used by the custom-language test.</summary>
    private static readonly string[] JustRo = ["ro"];

    /// <summary>CA1861 — single-entry retired-template expected-name set used by the retired test.</summary>
    private static readonly string[] LiveTemplateOnly = ["live-template"];

    /// <summary>CA1861 — paired (en, ru) language set used by gap-detection asserts.</summary>
    private static readonly string[] EnRu = ["en", "ru"];

    // ─────────────────── 1. ComputeCoverage — all 3 approved → no gap ───────────────────

    [Fact]
    public async Task ComputeCoverage_AllThreeApproved_NoGap()
    {
        var h = await Harness.CreateAsync();
        var template = await h.SeedTemplateAsync("aviz-emis");
        await h.SeedVariantAsync(template.Id, "ro", approved: true);
        await h.SeedVariantAsync(template.Id, "en", approved: true);
        await h.SeedVariantAsync(template.Id, "ru", approved: true);

        var result = await h.Service.ComputeCoverageAsync(
            new TemplateLanguageCoverageFilterDto());

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalTemplatesScanned.Should().Be(1);
        result.Value.TotalTemplatesFullyCovered.Should().Be(1);
        result.Value.TotalTemplatesWithGaps.Should().Be(0);
        result.Value.Gaps.Should().BeEmpty();
    }

    // ─────────────────── 2. Missing EN approved → gap with EN ───────────────────

    [Fact]
    public async Task ComputeCoverage_MissingEnApproved_GapIncludesEn()
    {
        var h = await Harness.CreateAsync();
        var template = await h.SeedTemplateAsync("decizia-pensie");
        await h.SeedVariantAsync(template.Id, "ro", approved: true);
        await h.SeedVariantAsync(template.Id, "ru", approved: true);
        // No EN variant.

        var result = await h.Service.ComputeCoverageAsync(
            new TemplateLanguageCoverageFilterDto());

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalTemplatesWithGaps.Should().Be(1);
        var gap = result.Value.Gaps.Single();
        gap.TemplateCode.Should().Be("decizia-pensie");
        gap.MissingLanguages.Should().ContainSingle().Which.Should().Be("en");
        gap.ExistingApprovedLanguages.Should().BeEquivalentTo(RoRu);
        gap.ExistingUnapprovedLanguages.Should().BeEmpty();
    }

    // ─────────────────── 3. OnlyApproved=false considers unapproved ───────────────────

    [Fact]
    public async Task ComputeCoverage_OnlyApprovedFalse_TreatsUnapprovedAsCovered()
    {
        var h = await Harness.CreateAsync();
        var template = await h.SeedTemplateAsync("act-control");
        await h.SeedVariantAsync(template.Id, "ro", approved: true);
        await h.SeedVariantAsync(template.Id, "en", approved: false); // unapproved
        await h.SeedVariantAsync(template.Id, "ru", approved: false); // unapproved

        // With OnlyApproved=true → EN + RU are gaps.
        var strict = await h.Service.ComputeCoverageAsync(
            new TemplateLanguageCoverageFilterDto(OnlyApproved: true));
        strict.IsSuccess.Should().BeTrue();
        strict.Value.TotalTemplatesWithGaps.Should().Be(1);

        // With OnlyApproved=false → no gaps (unapproved rows count as coverage).
        var lenient = await h.Service.ComputeCoverageAsync(
            new TemplateLanguageCoverageFilterDto(OnlyApproved: false));
        lenient.IsSuccess.Should().BeTrue();
        lenient.Value.TotalTemplatesWithGaps.Should().Be(0);
    }

    // ─────────────────── 4. IncludeRetiredTemplates=false excludes retired ───────────────────

    [Fact]
    public async Task ComputeCoverage_IncludeRetiredFalse_ExcludesRetired()
    {
        var h = await Harness.CreateAsync();
        var live = await h.SeedTemplateAsync("live-template");
        var retired = await h.SeedTemplateAsync("retired-template");
        retired.IsActive = false;
        await h.Db.SaveChangesAsync();

        // Neither has any variants, so live is a gap-template but retired
        // must be filtered out entirely when IncludeRetiredTemplates=false.
        var result = await h.Service.ComputeCoverageAsync(
            new TemplateLanguageCoverageFilterDto(IncludeRetiredTemplates: false));

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalTemplatesScanned.Should().Be(1);
        result.Value.Gaps.Select(g => g.TemplateCode).Should().BeEquivalentTo(LiveTemplateOnly);
        _ = live;
    }

    // ─────────────────── 5. Custom RequiredLanguages override ───────────────────

    [Fact]
    public async Task ComputeCoverage_CustomRequiredLanguages_OverridesDefault()
    {
        var h = await Harness.CreateAsync();
        var template = await h.SeedTemplateAsync("foo");
        await h.SeedVariantAsync(template.Id, "ro", approved: true);
        // No EN, no RU — but custom set is just "ro" so no gap.

        var result = await h.Service.ComputeCoverageAsync(
            new TemplateLanguageCoverageFilterDto(
                RequiredLanguages: JustRo,
                OnlyApproved: true));

        result.IsSuccess.Should().BeTrue();
        result.Value.RequiredLanguages.Should().BeEquivalentTo(JustRo);
        result.Value.TotalTemplatesWithGaps.Should().Be(0);
    }

    // ─────────────────── 6. RecordCoverageRunAsync inserts findings + audit + metric ───────────────────

    [Fact]
    public async Task RecordCoverageRunAsync_NewGaps_InsertsFindingsAndEmitsAudit()
    {
        // Snapshot the gap-detected metric via MeterListener.
        var metricByLanguage = new Dictionary<string, long>(StringComparer.Ordinal);
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instr, l) =>
        {
            if (instr.Meter.Name == CnasMeter.MeterName
                && instr.Name == "cnas.template.coverage.gap_detected")
            {
                l.EnableMeasurementEvents(instr);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            string? lang = null;
            foreach (var t in tags)
            {
                if (t.Key == "language") lang = t.Value?.ToString();
            }
            if (lang is not null)
            {
                metricByLanguage[lang] = metricByLanguage.GetValueOrDefault(lang) + value;
            }
        });
        listener.Start();

        var h = await Harness.CreateAsync();
        var template = await h.SeedTemplateAsync("alpha");
        await h.SeedVariantAsync(template.Id, "ro", approved: true);
        // EN + RU missing.

        var result = await h.Service.RecordCoverageRunAsync(
            new TemplateLanguageCoverageFilterDto());

        result.IsSuccess.Should().BeTrue();
        var findings = await h.Db.TemplateLanguageCoverageFindings.ToListAsync();
        findings.Should().HaveCount(2);
        findings.Select(f => f.MissingLanguage).Should().BeEquivalentTo(EnRu);
        findings.Should().OnlyContain(f => !f.Acknowledged);

        await h.Audit.Received(2).RecordAsync(
            "TEMPLATE.COVERAGE.GAP_DETECTED",
            AuditSeverity.Critical,
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<long?>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());

        metricByLanguage.Keys.Should().BeEquivalentTo(EnRu);
    }

    // ─────────────────── 7. RecordCoverageRunAsync second run does NOT duplicate findings ───────────────────

    [Fact]
    public async Task RecordCoverageRunAsync_RanTwice_NoDuplicateOpenFindings()
    {
        var h = await Harness.CreateAsync();
        var template = await h.SeedTemplateAsync("beta");
        await h.SeedVariantAsync(template.Id, "ro", approved: true);

        await h.Service.RecordCoverageRunAsync(new TemplateLanguageCoverageFilterDto());
        var afterFirst = await h.Db.TemplateLanguageCoverageFindings.CountAsync();
        await h.Service.RecordCoverageRunAsync(new TemplateLanguageCoverageFilterDto());
        var afterSecond = await h.Db.TemplateLanguageCoverageFindings.CountAsync();

        afterFirst.Should().Be(2);
        afterSecond.Should().Be(2);
    }

    // ─────────────────── 8. AcknowledgeFindingAsync flips flag + audit + metric ───────────────────

    [Fact]
    public async Task AcknowledgeFindingAsync_HappyPath_FlipsFlagAndAuditsAndIncrements()
    {
        var acknowledged = 0L;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instr, l) =>
        {
            if (instr.Meter.Name == CnasMeter.MeterName
                && instr.Name == "cnas.template.coverage.gap_acknowledged")
            {
                l.EnableMeasurementEvents(instr);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref acknowledged, value));
        listener.Start();

        var h = await Harness.CreateAsync();
        var template = await h.SeedTemplateAsync("gamma");
        var finding = new TemplateLanguageCoverageFinding
        {
            TemplateId = template.Id,
            MissingLanguage = "en",
            DetectedAt = ClockNow,
            Acknowledged = false,
            CreatedAtUtc = ClockNow,
            CreatedBy = "system",
            IsActive = true,
        };
        h.Db.TemplateLanguageCoverageFindings.Add(finding);
        await h.Db.SaveChangesAsync();

        var input = new TemplateLanguageCoverageAcknowledgeInputDto(
            Note: "Translation queued in batch 42.");
        var result = await h.Service.AcknowledgeFindingAsync(h.Encode(finding.Id), input);

        result.IsSuccess.Should().BeTrue();
        result.Value.Acknowledged.Should().BeTrue();
        var reloaded = await h.Db.TemplateLanguageCoverageFindings.SingleAsync();
        reloaded.Acknowledged.Should().BeTrue();
        reloaded.AcknowledgementNote.Should().Be("Translation queued in batch 42.");
        acknowledged.Should().BeGreaterThan(0);

        await h.Audit.Received(1).RecordAsync(
            "TEMPLATE.COVERAGE.GAP_ACKNOWLEDGED",
            AuditSeverity.Critical,
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<long?>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AcknowledgeFindingAsync_Twice_ReturnsConflict()
    {
        var h = await Harness.CreateAsync();
        var template = await h.SeedTemplateAsync("delta");
        var finding = new TemplateLanguageCoverageFinding
        {
            TemplateId = template.Id,
            MissingLanguage = "en",
            DetectedAt = ClockNow,
            Acknowledged = false,
            CreatedAtUtc = ClockNow,
            CreatedBy = "system",
            IsActive = true,
        };
        h.Db.TemplateLanguageCoverageFindings.Add(finding);
        await h.Db.SaveChangesAsync();

        var input = new TemplateLanguageCoverageAcknowledgeInputDto(Note: "Already done.");
        var first = await h.Service.AcknowledgeFindingAsync(h.Encode(finding.Id), input);
        first.IsSuccess.Should().BeTrue();

        var second = await h.Service.AcknowledgeFindingAsync(h.Encode(finding.Id), input);
        second.IsFailure.Should().BeTrue();
        second.ErrorCode.Should().Be(ErrorCodes.Conflict);
    }

    // ─────────────────── ListFindingsAsync ───────────────────

    [Fact]
    public async Task ListFindingsAsync_FilterByLanguage_ReturnsOnlyMatching()
    {
        var h = await Harness.CreateAsync();
        var template = await h.SeedTemplateAsync("epsilon");
        h.Db.TemplateLanguageCoverageFindings.AddRange(
            new TemplateLanguageCoverageFinding
            {
                TemplateId = template.Id, MissingLanguage = "en", DetectedAt = ClockNow,
                CreatedAtUtc = ClockNow, CreatedBy = "system", IsActive = true,
            },
            new TemplateLanguageCoverageFinding
            {
                TemplateId = template.Id, MissingLanguage = "ru", DetectedAt = ClockNow,
                CreatedAtUtc = ClockNow, CreatedBy = "system", IsActive = true,
            });
        await h.Db.SaveChangesAsync();

        var result = await h.Service.ListFindingsAsync(
            new TemplateLanguageCoverageFindingFilterDto(MissingLanguage: "ru"));

        result.IsSuccess.Should().BeTrue();
        result.Value.Total.Should().Be(1);
        result.Value.Items.Should().ContainSingle().Which.MissingLanguage.Should().Be("ru");
    }

    // ─────────────────── Test harness ───────────────────

    private sealed class Harness
    {
        public required CnasDbContext Db { get; init; }
        public required TemplateLanguageCoverageService Service { get; init; }
        public required IAuditService Audit { get; init; }
        public required ISqidService Sqids { get; init; }

        public string Encode(long id) => Sqids.Encode(id);

        public static Task<Harness> CreateAsync()
        {
            var opts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-template-coverage-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var db = new CnasDbContext(opts);
            var clock = new StubClock(ClockNow);
            var sqids = new FakeSqidService();
            var caller = Substitute.For<ICallerContext>();
            caller.UserId.Returns(7L);
            caller.UserSqid.Returns((string?)null); // system / scheduled
            caller.SourceIp.Returns("203.0.113.7");
            caller.CorrelationId.Returns("corr-coverage");
            var audit = Substitute.For<IAuditService>();
            audit.RecordAsync(
                    Arg.Any<string>(), Arg.Any<AuditSeverity>(), Arg.Any<string>(),
                    Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
                    Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Result.Success());
            var service = new TemplateLanguageCoverageService(
                db: db,
                audit: audit,
                sqids: sqids,
                clock: clock,
                caller: caller,
                filterValidator: new TemplateLanguageCoverageFilterValidator(),
                findingFilterValidator: new TemplateLanguageCoverageFindingFilterValidator(),
                ackValidator: new TemplateLanguageCoverageAcknowledgeInputValidator());

            return Task.FromResult(new Harness
            {
                Db = db,
                Service = service,
                Audit = audit,
                Sqids = sqids,
            });
        }

        public async Task<DocumentTemplate> SeedTemplateAsync(string code)
        {
            var row = new DocumentTemplate
            {
                Code = code,
                Name = code,
                Version = 1,
                IsCurrent = true,
                StorageObjectKey = $"templates/{code}/v1/{code}.docx",
                ContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                ContentLength = 4,
                ContentSha256 = "0".PadRight(64, '0'),
                CreatedAtUtc = ClockNow,
                IsActive = true,
                DefaultLanguage = "ro",
            };
            Db.DocumentTemplates.Add(row);
            await Db.SaveChangesAsync();
            return row;
        }

        public async Task<TemplateVariant> SeedVariantAsync(long templateId, string language, bool approved)
        {
            var v = new TemplateVariant
            {
                TemplateId = templateId,
                Language = language,
                SubjectOrTitle = $"subject-{language}",
                Body = $"body-{language}",
                IsApproved = approved,
                CreatedAtUtc = ClockNow,
                IsActive = true,
            };
            Db.TemplateVariants.Add(v);
            await Db.SaveChangesAsync();
            return v;
        }
    }

    /// <summary>Stub clock returning a fixed UTC instant.</summary>
    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }

    /// <summary>Round-trip fake Sqid service.</summary>
    private sealed class FakeSqidService : ISqidService
    {
        public string Encode(long id) => $"v_{id}";
        public Result<long> TryDecode(string? sqid)
        {
            if (sqid is not null && sqid.StartsWith("v_") && long.TryParse(sqid.AsSpan(2), out var id))
            {
                return Result<long>.Success(id);
            }
            return Result<long>.Failure(ErrorCodes.InvalidSqid, "bad fake sqid");
        }
    }
}
