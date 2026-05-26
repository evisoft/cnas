using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.E2E.Tests.Auth;
using Cnas.Ps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cnas.Ps.E2E.Tests.Journeys;

/// <summary>
/// UC08 — "Examinare document". End-to-end journey covering an examiner recording a
/// verdict on a citizen-attached document. Drives the real
/// <c>ExaminationController.RecordVerdictAsync</c> + <c>DocumentExaminationService</c>
/// stack through HTTP so the policy gate (<c>CnasUser</c>), the verdict persistence,
/// and the audit trail are all exercised together.
/// </summary>
/// <remarks>
/// <para>
/// <b>Actors.</b> Examiner — authenticated CNAS staff with the <c>cnas-user</c> role.
/// </para>
/// <para>
/// <b>Business outcome asserted.</b>
/// <list type="number">
///   <item>HTTP 200 OK from <c>POST /api/examination/documents/{id}/verdict</c>.</item>
///   <item>The <see cref="Document"/> row has <see cref="Document.Verdict"/> populated
///         with the supplied <see cref="Application.UseCases.ExaminationVerdict"/> value
///         and <see cref="Document.VerdictAtUtc"/> stamped.</item>
///   <item>An <see cref="AuditLog"/> row exists with <c>EventCode = "DOCUMENT.Accepted"</c>
///         and a target id pointing at the document — the cross-cutting audit-trail
///         coverage demanded by SEC 042 / CLAUDE.md §5.6.</item>
/// </list>
/// </para>
/// </remarks>
[Collection(AuthenticatedE2ECollection.Name)]
public sealed class Uc08_ExaminationVerdictJourneyTests
{
    private readonly AuthenticatedApiHostFixture _fixture;

    /// <summary>Injects the authenticated E2E host fixture.</summary>
    /// <param name="fixture">Shared collection fixture.</param>
    public Uc08_ExaminationVerdictJourneyTests(AuthenticatedApiHostFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// An examiner records a positive verdict on a citizen-uploaded attachment. The
    /// document row transitions to <c>Accepted</c> and an audit entry is journaled.
    /// </summary>
    [Fact]
    public async Task RecordVerdict_ExaminerAccepts_PersistsVerdictAndAuditsDecision()
    {
        // Arrange — seed an Attachment document directly in the DB. The journey deliberately
        // skips the file-upload path (MinIO is not available in the E2E host) and seeds the
        // metadata row with synthetic storage identifiers; the examiner-side flow under test
        // mutates DB columns only, never re-reads the binary content.
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CnasDbContext>();
        var sqids = scope.ServiceProvider.GetRequiredService<ISqidService>();

        var document = new Document
        {
            Kind = DocumentKind.Attachment,
            Title = "UC08 attachment",
            MimeType = "application/pdf",
            SizeBytes = 1024,
            StorageObjectKey = $"e2e-uc08/{Guid.NewGuid():N}",
            StorageBucket = "citizen-uploads",
            ContentSha256Hex = new string('0', 64),
            CreatedAtUtc = DateTime.UtcNow.AddMinutes(-5),
            IsActive = true,
        };
        db.Documents.Add(document);
        await db.SaveChangesAsync();

        var docSqid = sqids.Encode(document.Id);

        // Build the authenticated HTTP client for an examiner persona.
        using var client = new HttpClient { BaseAddress = new Uri(_fixture.BaseAddress) };
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HeaderName,
            TestPersonaHeader.Serialize(
                new TestPrincipal(Sub: sqids.Encode(900001), Roles: ["cnas-user"])));

        var verdictPayload = new { Verdict = "Accepted", Note = "Document complet — UC08 happy path." };

        // Act
        using var response = await client.PostAsJsonAsync(
            $"/api/examination/documents/{docSqid}/verdict",
            verdictPayload);

        // Assert — 200 OK from the controller (RecordVerdictAsync returns Ok() on success).
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());

        // Assert — document row has the verdict stamped. Fresh scope to bypass tracker cache.
        await using var readScope = _fixture.Services.CreateAsyncScope();
        var readDb = readScope.ServiceProvider.GetRequiredService<CnasDbContext>();
        var persisted = await readDb.Documents.AsNoTracking()
            .SingleOrDefaultAsync(d => d.Id == document.Id);
        persisted.Should().NotBeNull();
        persisted!.Verdict.Should().NotBeNull("the examiner verdict must persist on the document row");
        persisted.VerdictAtUtc.Should().NotBeNull("the timestamp is stamped together with the verdict");
        persisted.VerdictNote.Should().Be("Document complet — UC08 happy path.");

        // Assert — audit log carries the DOCUMENT.<Verdict> event for this document.
        var audit = await readDb.AuditLogs.AsNoTracking()
            .Where(a => a.TargetEntity == nameof(Document)
                && a.TargetEntityId == document.Id
                && a.EventCode == "DOCUMENT.Accepted")
            .SingleOrDefaultAsync();
        audit.Should().NotBeNull(
            "every examiner verdict must produce an audit-log row per SEC 042 / CLAUDE.md §5.6");
        audit!.ActorId.Should().NotBeNull();
    }
}
