using System.Text;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Templates;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// R0133 / TOR CF 17.16 — Unit tests for <see cref="TemplateVariantService"/> and
/// <see cref="TemplateVariantResolver"/>. Asserts the upsert / approval lifecycle,
/// the per-locale resolver fallback (including the
/// <c>cnas.template.render.fallback</c> counter), and the boundary validators for
/// language code + DOCX magic bytes.
/// </summary>
public sealed class TemplateVariantServiceTests
{
    private static readonly DateTime ClockNow = new(2026, 5, 21, 12, 0, 0, DateTimeKind.Utc);

    // ─────────────────── Variant service — upsert / approve lifecycle ───────────────────

    [Fact]
    public async Task UpsertAsync_NewVariant_InsertsRowUnapproved()
    {
        var h = await Harness.CreateAsync();
        var template = await h.SeedTemplateAsync("decizia-pensie", defaultLanguage: TemplateLanguages.Ro);

        var dto = new TemplateVariantUpsertDto(
            TemplateSqid: h.Encode(template.Id),
            Language: TemplateLanguages.En,
            SubjectOrTitle: "Pension decision",
            Body: "Hello {{name}}",
            TranslatorNote: "first pass");

        var result = await h.Service.UpsertAsync(dto);

        result.IsSuccess.Should().BeTrue();
        result.Value.Language.Should().Be(TemplateLanguages.En);
        result.Value.IsApproved.Should().BeFalse();
        result.Value.HasDocx.Should().BeFalse();

        var row = await h.Db.TemplateVariants.SingleAsync();
        row.TemplateId.Should().Be(template.Id);
        row.IsApproved.Should().BeFalse();
        row.SubjectOrTitle.Should().Be("Pension decision");
    }

    [Fact]
    public async Task UpsertAsync_ExistingPair_ReplacesInPlace()
    {
        var h = await Harness.CreateAsync();
        var template = await h.SeedTemplateAsync("aviz-final");

        var v1 = new TemplateVariantUpsertDto(h.Encode(template.Id), TemplateLanguages.Ru, "v1", "body v1");
        (await h.Service.UpsertAsync(v1)).IsSuccess.Should().BeTrue();

        var v2 = new TemplateVariantUpsertDto(h.Encode(template.Id), TemplateLanguages.Ru, "v2", "body v2");
        var r2 = await h.Service.UpsertAsync(v2);

        r2.IsSuccess.Should().BeTrue();
        (await h.Db.TemplateVariants.CountAsync()).Should().Be(1);
        var row = await h.Db.TemplateVariants.SingleAsync();
        row.SubjectOrTitle.Should().Be("v2");
        row.Body.Should().Be("body v2");
    }

    [Fact]
    public async Task UpsertAsync_UnsupportedLanguage_ReturnsValidationFailed()
    {
        var h = await Harness.CreateAsync();
        var template = await h.SeedTemplateAsync("any");

        var dto = new TemplateVariantUpsertDto(h.Encode(template.Id), "de", "S", "B");

        var r = await h.Service.UpsertAsync(dto);

        r.IsFailure.Should().BeTrue();
        r.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task UpsertAsync_DocxBadMagicBytes_ReturnsFileTypeMismatch()
    {
        var h = await Harness.CreateAsync();
        var template = await h.SeedTemplateAsync("any");
        // 'XXXX' bytes — not the ZIP/DOCX magic header.
        var bogus = Convert.ToBase64String([0x58, 0x58, 0x58, 0x58, 0x00, 0x01]);

        var dto = new TemplateVariantUpsertDto(
            h.Encode(template.Id), TemplateLanguages.En, "S", "B",
            DocxBase64: bogus);

        var r = await h.Service.UpsertAsync(dto);

        r.IsFailure.Should().BeTrue();
        r.ErrorCode.Should().Be(ErrorCodes.FileTypeMismatch);
    }

    [Fact]
    public async Task ApproveAsync_FlipsFlagAndEmitsCriticalAudit()
    {
        var h = await Harness.CreateAsync();
        var template = await h.SeedTemplateAsync("decizia");
        var dto = new TemplateVariantUpsertDto(h.Encode(template.Id), TemplateLanguages.En, "Decision", "Body");
        var upserted = await h.Service.UpsertAsync(dto);
        upserted.IsSuccess.Should().BeTrue();
        var variantId = h.Decode(upserted.Value.Id);

        var r = await h.Service.ApproveAsync(variantId);

        r.IsSuccess.Should().BeTrue();
        (await h.Db.TemplateVariants.SingleAsync(v => v.Id == variantId)).IsApproved.Should().BeTrue();
        await h.Audit.Received(1).RecordAsync(
            "TEMPLATE.VARIANT.APPROVED",
            AuditSeverity.Critical,
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<long?>(),
            Arg.Is<string>(d => d.Contains("decizia") && d.Contains("en")),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    // ─────────────────── Variant resolver — fallback semantics ───────────────────

    [Fact]
    public async Task Resolver_RequestedLanguageUnapproved_FallsBackAndIncrementsCounter()
    {
        var h = await Harness.CreateAsync();
        var template = await h.SeedTemplateAsync("decizia-pensie", defaultLanguage: TemplateLanguages.Ro);
        // Approved RO variant.
        await h.SeedVariantAsync(template.Id, TemplateLanguages.Ro, "RO subiect", "RO body", approved: true);
        // EN variant exists but is NOT approved.
        await h.SeedVariantAsync(template.Id, TemplateLanguages.En, "EN subject", "EN body", approved: false);

        using var capture = new TaggedMetricCapture("cnas.template.render.fallback");

        var resolved = await h.Resolver.ResolveAsync(template.Id, TemplateLanguages.En);

        resolved.IsSuccess.Should().BeTrue();
        resolved.Value.Language.Should().Be(TemplateLanguages.Ro);
        resolved.Value.SubjectOrTitle.Should().Be("RO subiect");
        resolved.Value.FellBack.Should().BeTrue();
        capture.Total.Should().Be(1);
        capture.FromValues.Should().Contain("en");
        capture.ToValues.Should().Contain("ro");
    }

    [Fact]
    public async Task Resolver_RequestedLanguageMissing_FallsBackToDefault()
    {
        var h = await Harness.CreateAsync();
        var template = await h.SeedTemplateAsync("decizia-pensie", defaultLanguage: TemplateLanguages.Ro);
        await h.SeedVariantAsync(template.Id, TemplateLanguages.Ro, "RO subiect", "RO body", approved: true);
        // No EN variant at all.

        var resolved = await h.Resolver.ResolveAsync(template.Id, TemplateLanguages.En);

        resolved.IsSuccess.Should().BeTrue();
        resolved.Value.Language.Should().Be(TemplateLanguages.Ro);
        resolved.Value.FellBack.Should().BeTrue();
    }

    [Fact]
    public async Task Resolver_RequestedLanguageApproved_UsesIt()
    {
        var h = await Harness.CreateAsync();
        var template = await h.SeedTemplateAsync("decizia-pensie", defaultLanguage: TemplateLanguages.Ro);
        await h.SeedVariantAsync(template.Id, TemplateLanguages.Ro, "RO", "RO body", approved: true);
        await h.SeedVariantAsync(template.Id, TemplateLanguages.En, "EN", "EN body", approved: true);

        var resolved = await h.Resolver.ResolveAsync(template.Id, TemplateLanguages.En);

        resolved.IsSuccess.Should().BeTrue();
        resolved.Value.Language.Should().Be(TemplateLanguages.En);
        resolved.Value.SubjectOrTitle.Should().Be("EN");
        resolved.Value.FellBack.Should().BeFalse();
    }

    // ─────────────────── Catalog port — export / import round-trip ───────────────────

    [Fact]
    public async Task Port_ExportXml_ImportXml_RoundTrip()
    {
        var h = await Harness.CreateAsync();
        var t = await h.SeedTemplateAsync("xml-round", defaultLanguage: TemplateLanguages.Ro);
        await h.SeedVariantAsync(t.Id, TemplateLanguages.Ro, "Subj-RO", "Body-RO", approved: true);
        await h.SeedVariantAsync(t.Id, TemplateLanguages.En, "Subj-EN", "Body-EN", approved: false);

        var exported = await h.Port.ExportXmlAsync();
        exported.IsSuccess.Should().BeTrue();

        // Mutate the body of the RO variant in-place to confirm import actually updates.
        var ro = await h.Db.TemplateVariants.SingleAsync(v => v.TemplateId == t.Id && v.Language == "ro");
        ro.Body = "STALE";
        await h.Db.SaveChangesAsync();

        using var ms = new MemoryStream(exported.Value);
        var report = await h.Port.ImportXmlAsync(ms);

        report.IsSuccess.Should().BeTrue();
        report.Value.Errors.Should().BeEmpty();
        report.Value.Updated.Should().Be(2);
        var refreshed = await h.Db.TemplateVariants.AsNoTracking()
            .SingleAsync(v => v.TemplateId == t.Id && v.Language == "ro");
        refreshed.Body.Should().Be("Body-RO");
    }

    [Fact]
    public async Task Port_ExportCsv_ImportCsv_RoundTrip()
    {
        var h = await Harness.CreateAsync();
        var t = await h.SeedTemplateAsync("csv-round");
        await h.SeedVariantAsync(t.Id, TemplateLanguages.Ro, "Subj, with comma", "Body line1\nLine2 \"quoted\"", approved: true);

        var exported = await h.Port.ExportCsvAsync();
        exported.IsSuccess.Should().BeTrue();

        using var ms = new MemoryStream(exported.Value);
        var report = await h.Port.ImportCsvAsync(ms);

        report.IsSuccess.Should().BeTrue();
        report.Value.Errors.Should().BeEmpty();
        report.Value.Updated.Should().Be(1);
        var refreshed = await h.Db.TemplateVariants.AsNoTracking().SingleAsync(v => v.TemplateId == t.Id);
        refreshed.SubjectOrTitle.Should().Be("Subj, with comma");
        refreshed.Body.Should().Contain("\"quoted\"");
    }

    [Fact]
    public async Task Port_ImportXml_UnknownTemplate_SkipsWithWarning()
    {
        var h = await Harness.CreateAsync();
        // No templates seeded.
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <TemplateCatalog>
              <Template code="does-not-exist" defaultLanguage="ro">
                <Variant language="ro" subject="X" approved="true"><![CDATA[hello]]></Variant>
              </Template>
            </TemplateCatalog>
            """;

        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var report = await h.Port.ImportXmlAsync(ms);

        report.IsSuccess.Should().BeTrue();
        report.Value.Skipped.Should().Be(1);
        report.Value.Warnings.Should().NotBeEmpty();
        report.Value.Warnings[0].Should().Contain("does-not-exist");
        report.Value.Created.Should().Be(0);
        report.Value.Updated.Should().Be(0);
    }

    [Fact]
    public async Task Port_ImportCsv_OneBadRow_AbortsWholeImport()
    {
        var h = await Harness.CreateAsync();
        var t = await h.SeedTemplateAsync("ok-template");

        // Header + one VALID row + one INVALID row (language = 'de').
        var csv = "TemplateCode,Language,Subject,Body,Approved,TranslatorNote\n"
            + "ok-template,ro,Good,Body,true,\n"
            + "ok-template,de,Bad,Body,true,\n";
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(csv));

        var report = await h.Port.ImportCsvAsync(ms);

        report.IsFailure.Should().BeTrue();
        report.ErrorCode.Should().Be(ErrorCodes.ImportValidationFailed);
        // Nothing persisted — all-or-nothing.
        (await h.Db.TemplateVariants.CountAsync(v => v.TemplateId == t.Id)).Should().Be(0);
    }

    [Fact]
    public async Task Port_ImportXml_Success_EmitsCatalogImportedAudit()
    {
        var h = await Harness.CreateAsync();
        var t = await h.SeedTemplateAsync("audit-template");

        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <TemplateCatalog>
              <Template code="audit-template" defaultLanguage="ro">
                <Variant language="ro" subject="S" approved="true"><![CDATA[B]]></Variant>
              </Template>
            </TemplateCatalog>
            """;

        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var report = await h.Port.ImportXmlAsync(ms);

        report.IsSuccess.Should().BeTrue();
        await h.Audit.Received(1).RecordAsync(
            "TEMPLATE.CATALOG.IMPORTED",
            AuditSeverity.Critical,
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<long?>(),
            Arg.Is<string>(s => s.Contains("\"Created\"") || s.Contains("Created")),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    // ─────────────────── Test harness ───────────────────

    private sealed class Harness
    {
        public required CnasDbContext Db { get; init; }
        public required TemplateVariantService Service { get; init; }
        public required TemplateVariantResolver Resolver { get; init; }
        public required TemplateCatalogPort Port { get; init; }
        public required IAuditService Audit { get; init; }
        public required ISqidService Sqids { get; init; }

        public string Encode(long id) => Sqids.Encode(id);
        public long Decode(string sqid) => Sqids.TryDecode(sqid).Value;

        public static Task<Harness> CreateAsync()
        {
            var opts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-template-variants-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var db = new CnasDbContext(opts);
            var clock = new StubClock(ClockNow);
            var sqids = new FakeSqidService();
            var caller = Substitute.For<ICallerContext>();
            caller.UserSqid.Returns("admin-1");
            caller.SourceIp.Returns("127.0.0.1");
            caller.CorrelationId.Returns("corr-test");
            var audit = Substitute.For<IAuditService>();
            audit.RecordAsync(
                    Arg.Any<string>(), Arg.Any<AuditSeverity>(), Arg.Any<string>(),
                    Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
                    Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                 .Returns(Result.Success());
            var service = new TemplateVariantService(db, clock, sqids, caller, audit);
            var resolver = new TemplateVariantResolver(db);
            var port = new TemplateCatalogPort(db, clock, caller, audit);
            return Task.FromResult(new Harness
            {
                Db = db,
                Service = service,
                Resolver = resolver,
                Port = port,
                Audit = audit,
                Sqids = sqids,
            });
        }

        public async Task<DocumentTemplate> SeedTemplateAsync(string code, string defaultLanguage = "ro")
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
                DefaultLanguage = defaultLanguage,
            };
            Db.DocumentTemplates.Add(row);
            await Db.SaveChangesAsync();
            return row;
        }

        public async Task<TemplateVariant> SeedVariantAsync(
            long templateId,
            string language,
            string subject,
            string body,
            bool approved)
        {
            var v = new TemplateVariant
            {
                TemplateId = templateId,
                Language = language,
                SubjectOrTitle = subject,
                Body = body,
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

    /// <summary>
    /// Round-trip fake Sqid service used by the variant tests. Uses a simple
    /// "v_{id}" / "v_{id}" mapping which is reversible without pulling in the
    /// production Sqids library.
    /// </summary>
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

    /// <summary>
    /// Captures cnas.template.render.fallback measurements plus their <c>from</c> /
    /// <c>to</c> tag values via <see cref="System.Diagnostics.Metrics.MeterListener"/>.
    /// </summary>
    private sealed class TaggedMetricCapture : IDisposable
    {
        private readonly System.Diagnostics.Metrics.MeterListener _listener;
        private readonly List<long> _values = new();
        private readonly List<string?> _from = new();
        private readonly List<string?> _to = new();
        private readonly object _gate = new();

        public long Total { get { lock (_gate) { return _values.Sum(); } } }
        public IReadOnlyList<string?> FromValues { get { lock (_gate) { return _from.ToArray(); } } }
        public IReadOnlyList<string?> ToValues { get { lock (_gate) { return _to.ToArray(); } } }

        public TaggedMetricCapture(string instrumentName)
        {
            _listener = new System.Diagnostics.Metrics.MeterListener
            {
                InstrumentPublished = (instr, l) =>
                {
                    if (instr.Meter.Name == CnasMeter.MeterName && instr.Name == instrumentName)
                    {
                        l.EnableMeasurementEvents(instr);
                    }
                },
            };
            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                string? from = null, to = null;
                foreach (var tag in tags)
                {
                    if (tag.Key == "from") from = tag.Value?.ToString();
                    if (tag.Key == "to") to = tag.Value?.ToString();
                }
                lock (_gate)
                {
                    _values.Add(value);
                    _from.Add(from);
                    _to.Add(to);
                }
            });
            _listener.Start();
        }

        public void Dispose() => _listener.Dispose();
    }
}
