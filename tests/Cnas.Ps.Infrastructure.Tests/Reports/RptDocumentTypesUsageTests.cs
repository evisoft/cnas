using System.Globalization;
using System.Text;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Reports;

/// <summary>
/// Integration tests for the Annex 6g report <c>RPT-DOCUMENT-TYPES-USAGE</c> — count of
/// <see cref="Document"/> rows created in <c>[fromUtc, toUtc)</c>, grouped by document type.
/// The "document type" is the <see cref="Document.Kind"/> enum stringified (a dedicated
/// <c>DocumentType</c> field does not exist in the present data model — see report XML doc).
/// Rows are ordered by Count desc, then DocumentType (Ordinal).
/// </summary>
public class RptDocumentTypesUsageTests
{
    /// <summary>Fixed UTC clock so date arithmetic is deterministic.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 20, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>The stable report code under test.</summary>
    private const string Code = "RPT-DOCUMENT-TYPES-USAGE";

    /// <summary>Locks the report's column shape (aggregated — no sqid fields).</summary>
    [Fact]
    public async Task Definition_HasExpectedCodeAndColumns()
    {
        var harness = Harness.Create();

        var paramsJson = $"{{ \"fromUtc\": \"{ClockNow.AddDays(-30):O}\", \"toUtc\": \"{ClockNow:O}\" }}";
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var firstLine = text.Split("\r\n")[0];
        firstLine.Should().Be("Document Type,Count");
    }

    /// <summary>
    /// Seeds three documents of different kinds in window plus one out-of-window doc that
    /// must be excluded. Verifies the per-kind counts.
    /// </summary>
    [Fact]
    public async Task Execute_WithSeededData_GroupsByKind()
    {
        var harness = Harness.Create();
        await harness.SeedDocumentAsync(DocumentKind.Attachment, ClockNow.AddDays(-1));
        await harness.SeedDocumentAsync(DocumentKind.Attachment, ClockNow.AddDays(-5));
        await harness.SeedDocumentAsync(DocumentKind.Decision, ClockNow.AddDays(-2));
        await harness.SeedDocumentAsync(DocumentKind.Certificate, ClockNow.AddDays(-3));
        // Out of window — must be excluded.
        await harness.SeedDocumentAsync(DocumentKind.Decision, ClockNow.AddDays(-100));

        var paramsJson = $"{{ \"fromUtc\": \"{ClockNow.AddDays(-30):O}\", \"toUtc\": \"{ClockNow:O}\" }}";
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        // Count desc tie-broken by name ordinal: Attachment=2, Certificate=1, Decision=1.
        lines.Should().Contain("Attachment,2");
        lines.Should().Contain("Certificate,1");
        lines.Should().Contain("Decision,1");
    }

    /// <summary>An empty window produces only the header row — no aggregate rows.</summary>
    [Fact]
    public async Task Execute_EmptyWindow_EmitsOnlyHeader()
    {
        var harness = Harness.Create();

        var paramsJson = $"{{ \"fromUtc\": \"{ClockNow.AddDays(-30):O}\", \"toUtc\": \"{ClockNow:O}\" }}";
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(1);
        lines[0].Should().Be("Document Type,Count");
    }

    /// <summary>Missing window parameters must be rejected with <see cref="ErrorCodes.ValidationFailed"/>.</summary>
    [Fact]
    public async Task Execute_MissingParameters_ReturnsValidationFailed()
    {
        var harness = Harness.Create();
        var result = await harness.Service.GenerateAsync(Code, "{}", ExportFormat.Csv);
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
    }

    // ─────────────────────── Helpers ───────────────────────

    /// <summary>Reads the full text of a stream using UTF-8 with BOM detection.</summary>
    private static string ReadAllText(Stream stream)
    {
        stream.Position = 0;
        using var sr = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        return sr.ReadToEnd();
    }

    /// <summary>Deterministic stub clock.</summary>
    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        /// <inheritdoc />
        public DateTime UtcNow { get; } = now;
    }

    /// <summary>Test harness composing EF Core InMemory + ReportingService.</summary>
    private sealed class Harness
    {
        /// <summary>The in-memory database context.</summary>
        public required CnasDbContext Db { get; init; }

        /// <summary>The system under test.</summary>
        public required ReportingService Service { get; init; }

        /// <summary>Creates a fresh, isolated harness for one test.</summary>
        public static Harness Create()
        {
            var opts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-rpt-doctypes-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var db = new CnasDbContext(opts);
            var sqids = Substitute.For<ISqidService>();
            sqids.Encode(Arg.Any<long>()).Returns(call => $"sqid-{call.Arg<long>()}");
            var clock = new StubClock(ClockNow);
            var service = new ReportingService(db, clock, sqids, NullLogger<ReportingService>.Instance, IdHashHelper.Instance);
            return new Harness { Db = db, Service = service };
        }

        /// <summary>
        /// Seeds an unattached <see cref="Document"/> (no <see cref="Document.DossierId"/>)
        /// of the supplied kind with the supplied <see cref="AuditableEntity.CreatedAtUtc"/>.
        /// "Unattached" is fine — the report does not require a dossier link.
        /// </summary>
        public async Task SeedDocumentAsync(DocumentKind kind, DateTime createdUtc)
        {
            Db.Documents.Add(new Document
            {
                CreatedAtUtc = createdUtc,
                DossierId = null,
                Kind = kind,
                Title = $"Doc-{Guid.NewGuid():N}"[..12],
                MimeType = "application/pdf",
                SizeBytes = 1024,
                StorageObjectKey = $"obj-{Guid.NewGuid():N}",
                StorageBucket = "test",
                ContentSha256Hex = new string('a', 64),
                IsActive = true,
            });
            await Db.SaveChangesAsync();
            _ = CultureInfo.InvariantCulture;
        }
    }
}
