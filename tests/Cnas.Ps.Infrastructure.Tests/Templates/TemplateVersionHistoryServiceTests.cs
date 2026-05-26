using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Templates;

/// <summary>
/// R0132 / CF 17.18 — service-level tests for template-version history list + diff +
/// rollback. Backed by an EF Core InMemory store.
/// </summary>
public sealed class TemplateVersionHistoryServiceTests
{
    private static readonly DateTime BaseUtc = new(2026, 5, 23, 10, 0, 0, DateTimeKind.Utc);

    private static CnasDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-tplhist-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static (TemplateVersionHistoryService Svc, CnasDbContext Db) Build(CnasDbContext db)
    {
        var sqids = Substitute.For<ISqidService>();
        sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");
        sqids.TryDecode(Arg.Any<string?>()).Returns(call =>
        {
            var s = call.Arg<string?>();
            if (s is not null && s.StartsWith("SQID-", StringComparison.Ordinal)
                && long.TryParse(s.AsSpan(5), out var id))
            {
                return Result<long>.Success(id);
            }
            return Result<long>.Failure(ErrorCodes.InvalidSqid, "bad");
        });

        var clock = Substitute.For<ICnasTimeProvider>();
        var nextOffset = 0;
        clock.UtcNow.Returns(_ => BaseUtc.AddSeconds(Interlocked.Increment(ref nextOffset)));

        var caller = Substitute.For<ICallerContext>();
        caller.UserSqid.Returns("SQID-99");
        caller.SourceIp.Returns("127.0.0.1");
        caller.CorrelationId.Returns("corr-1");

        var audit = Substitute.For<IAuditService>();
        audit.RecordAsync(
                Arg.Any<string>(), Arg.Any<AuditSeverity>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));

        var validator = new TemplateRollbackInputDtoValidator();

        var svc = new TemplateVersionHistoryService(
            db, sqids, clock, caller, audit, validator,
            NullLogger<TemplateVersionHistoryService>.Instance);
        return (svc, db);
    }

    /// <summary>Seeds three versions of one template plus an unrelated template.</summary>
    private static async Task SeedThreeVersionsAsync(CnasDbContext db, string code = "decizia-pensie")
    {
        db.DocumentTemplates.AddRange(
            new DocumentTemplate
            {
                Id = 1, Code = code, Name = "Decizia (v1)", Description = "v1",
                Version = 1, IsCurrent = false, IsActive = true,
                StorageObjectKey = $"templates/{code}/v1/{code}.docx",
                ContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                ContentLength = 1000, ContentSha256 = new string('a', 64),
                DefaultLanguage = "ro", CreatedAtUtc = BaseUtc,
            },
            new DocumentTemplate
            {
                Id = 2, Code = code, Name = "Decizia (v2)", Description = "v2",
                Version = 2, IsCurrent = false, IsActive = true,
                StorageObjectKey = $"templates/{code}/v2/{code}.docx",
                ContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                ContentLength = 2000, ContentSha256 = new string('b', 64),
                DefaultLanguage = "ro", CreatedAtUtc = BaseUtc.AddDays(1),
            },
            new DocumentTemplate
            {
                Id = 3, Code = code, Name = "Decizia (v3)", Description = "v3",
                Version = 3, IsCurrent = true, IsActive = true,
                StorageObjectKey = $"templates/{code}/v3/{code}.docx",
                ContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                ContentLength = 3000, ContentSha256 = new string('c', 64),
                DefaultLanguage = "ro", CreatedAtUtc = BaseUtc.AddDays(2),
            },
            new DocumentTemplate
            {
                Id = 99, Code = "unrelated-template", Name = "Other",
                Version = 1, IsCurrent = true, IsActive = true,
                StorageObjectKey = "templates/unrelated-template/v1/unrelated-template.docx",
                ContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                ContentLength = 50, ContentSha256 = new string('z', 64),
                DefaultLanguage = "ro", CreatedAtUtc = BaseUtc,
            });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task ListVersionsAsync_ReturnsAllVersionsForCode_DescendingVersion()
    {
        using var db = CreateContext();
        var (svc, _) = Build(db);
        await SeedThreeVersionsAsync(db);

        var result = await svc.ListVersionsAsync("decizia-pensie", skip: 0, take: 50);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalCount.Should().Be(3);
        result.Value.Items.Should().HaveCount(3);
        result.Value.Items[0].Version.Should().Be(3);
        result.Value.Items[1].Version.Should().Be(2);
        result.Value.Items[2].Version.Should().Be(1);
        // Sqid round-trip on the id.
        result.Value.Items[0].Id.Should().Be("SQID-3");
    }

    [Fact]
    public async Task DiffAsync_BetweenTwoVersions_ProducesModifiedEntries()
    {
        using var db = CreateContext();
        var (svc, _) = Build(db);
        await SeedThreeVersionsAsync(db);

        var result = await svc.DiffAsync(baselineVersionSqid: "SQID-1", currentVersionSqid: "SQID-3");

        result.IsSuccess.Should().BeTrue();
        result.Value.BaselineVersion.Should().Be(1);
        result.Value.CurrentVersion.Should().Be(3);
        result.Value.Entries.Should().NotBeEmpty();
        // Name + ContentSha256 + ContentLength + Description all differ between v1 and v3.
        result.Value.Entries.Should().Contain(e => e.FieldPath == "Name" && e.ChangeKind == "Modified");
        result.Value.Entries.Should().Contain(e => e.FieldPath == "ContentSha256");
        result.Value.Entries.Should().Contain(e => e.FieldPath == "ContentLength");
    }

    [Fact]
    public async Task RollbackToAsync_CreatesNewCurrentVersion_OriginalTargetUntouched()
    {
        using var db = CreateContext();
        var (svc, _) = Build(db);
        await SeedThreeVersionsAsync(db);

        var result = await svc.RollbackToAsync(
            targetVersionSqid: "SQID-1",
            input: new TemplateRollbackInputDto("Reverting after broken merge."));

        result.IsSuccess.Should().BeTrue();
        result.Value.Version.Should().Be(4);
        result.Value.IsCurrent.Should().BeTrue();
        // The new row copied content from v1.
        result.Value.ContentSha256.Should().Be(new string('a', 64));
        result.Value.ContentLength.Should().Be(1000);

        var allRows = await db.DocumentTemplates.AsNoTracking()
            .Where(t => t.Code == "decizia-pensie")
            .OrderBy(t => t.Version)
            .ToListAsync();
        allRows.Should().HaveCount(4);
        // Target (v1) row untouched: same Sha256, same IsCurrent=false.
        var target = allRows.Single(t => t.Version == 1);
        target.ContentSha256.Should().Be(new string('a', 64));
        target.IsCurrent.Should().BeFalse();
        // Previous current (v3) demoted.
        allRows.Single(t => t.Version == 3).IsCurrent.Should().BeFalse();
        // New v4 is current.
        allRows.Single(t => t.Version == 4).IsCurrent.Should().BeTrue();
    }
}
