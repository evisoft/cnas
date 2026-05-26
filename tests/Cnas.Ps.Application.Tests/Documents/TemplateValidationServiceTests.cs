using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Templates;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace Cnas.Ps.Application.Tests.Documents;

/// <summary>
/// R0131 / CF 17.15 — TDD coverage of <see cref="ITemplateValidationService"/>'s
/// metadata-driven validation gate. Wires the service against an EF Core InMemory
/// store through a minimal <see cref="IReadOnlyCnasDbContext"/> facade.
/// </summary>
public sealed class TemplateValidationServiceTests
{
    /// <summary>
    /// Minimal in-memory <see cref="DbContext"/> exposing only the
    /// <see cref="DocumentTemplate"/> set so the validation service can issue async
    /// queries.
    /// </summary>
    private sealed class StubDb : DbContext
    {
        public StubDb(DbContextOptions<StubDb> opts) : base(opts) { }

        public DbSet<DocumentTemplate> DocumentTemplates => Set<DocumentTemplate>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // The validation service only touches DocumentTemplate; suppress the rest.
            modelBuilder.Entity<DocumentTemplate>().HasKey(t => t.Id);
        }
    }

    /// <summary>
    /// Builds a minimal read-only context substitute that backs the
    /// <c>DocumentTemplates</c> queryable with a real EF Core InMemory store. All
    /// other members of the interface remain unimplemented — the service only
    /// touches this single property.
    /// </summary>
    private static (IReadOnlyCnasDbContext Ctx, StubDb Db) BuildContext(string templateCode, string? rulesJson)
    {
        var opts = new DbContextOptionsBuilder<StubDb>()
            .UseInMemoryDatabase($"tplval-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        var db = new StubDb(opts);
        db.DocumentTemplates.Add(new DocumentTemplate
        {
            Code = templateCode,
            Name = "tpl",
            Version = 1,
            IsCurrent = true,
            IsActive = true,
            StorageObjectKey = "k",
            ContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ContentLength = 1,
            ContentSha256 = new string('a', 64),
            DefaultLanguage = "ro",
            ValidationRulesJson = rulesJson,
            CreatedAtUtc = new DateTime(2026, 5, 24, 10, 0, 0, DateTimeKind.Utc),
        });
        db.SaveChanges();

        var ctx = Substitute.For<IReadOnlyCnasDbContext>();
        ctx.DocumentTemplates.Returns(_ => db.DocumentTemplates);
        return (ctx, db);
    }

    [Fact]
    public async Task ValidateAsync_NoRules_ReturnsSuccess()
    {
        var (ctx, _) = BuildContext("tpl-a", rulesJson: null);
        var svc = new TemplateValidationService(ctx);

        var result = await svc.ValidateAsync("tpl-a",
            new Dictionary<string, string?> { ["x"] = "y" });

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task ValidateAsync_RequiredMissing_ReturnsFailure()
    {
        const string json = """
            [ { "fieldName": "name", "ruleKind": "Required" } ]
            """;
        var (ctx, _) = BuildContext("tpl-req", rulesJson: json);
        var svc = new TemplateValidationService(ctx);

        var result = await svc.ValidateAsync("tpl-req",
            new Dictionary<string, string?> { ["name"] = "  " }); // whitespace ⇒ missing

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.TemplateValidationFailed);
        result.ErrorMessage.Should().Contain("name");
    }

    [Fact]
    public async Task ValidateAsync_MaxLengthExceeded_ReturnsFailure()
    {
        const string json = """
            [ { "fieldName": "code", "ruleKind": "MaxLength", "argument": "3" } ]
            """;
        var (ctx, _) = BuildContext("tpl-maxlen", rulesJson: json);
        var svc = new TemplateValidationService(ctx);

        var result = await svc.ValidateAsync("tpl-maxlen",
            new Dictionary<string, string?> { ["code"] = "ABCD" });

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.TemplateValidationFailed);
        result.ErrorMessage.Should().Contain("MaxLength");
    }

    [Fact]
    public async Task ValidateAsync_MinLengthUnderflow_ReturnsFailure()
    {
        const string json = """
            [ { "fieldName": "code", "ruleKind": "MinLength", "argument": "5" } ]
            """;
        var (ctx, _) = BuildContext("tpl-minlen", rulesJson: json);
        var svc = new TemplateValidationService(ctx);

        var result = await svc.ValidateAsync("tpl-minlen",
            new Dictionary<string, string?> { ["code"] = "AB" });

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.TemplateValidationFailed);
        result.ErrorMessage.Should().Contain("MinLength");
    }

    [Fact]
    public async Task ValidateAsync_RegexMismatch_ReturnsFailure()
    {
        const string json = """
            [ { "fieldName": "code", "ruleKind": "Regex", "argument": "^[A-Z]{3}$" } ]
            """;
        var (ctx, _) = BuildContext("tpl-regex", rulesJson: json);
        var svc = new TemplateValidationService(ctx);

        var result = await svc.ValidateAsync("tpl-regex",
            new Dictionary<string, string?> { ["code"] = "abcd" });

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.TemplateValidationFailed);
        result.ErrorMessage.Should().Contain("Regex");
    }

    [Fact]
    public async Task ValidateAsync_RangeViolation_ReturnsFailure()
    {
        const string json = """
            [ { "fieldName": "score", "ruleKind": "Range", "argument": "0..100" } ]
            """;
        var (ctx, _) = BuildContext("tpl-range", rulesJson: json);
        var svc = new TemplateValidationService(ctx);

        var result = await svc.ValidateAsync("tpl-range",
            new Dictionary<string, string?> { ["score"] = "150" });

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.TemplateValidationFailed);
        result.ErrorMessage.Should().Contain("Range");
    }

    [Fact]
    public async Task ValidateAsync_AllPass_ReturnsSuccess()
    {
        const string json = """
            [
                { "fieldName": "name", "ruleKind": "Required" },
                { "fieldName": "code", "ruleKind": "MaxLength", "argument": "10" },
                { "fieldName": "code", "ruleKind": "MinLength", "argument": "2" },
                { "fieldName": "code", "ruleKind": "Regex", "argument": "^[A-Z]+$" },
                { "fieldName": "score", "ruleKind": "Range", "argument": "0..100" }
            ]
            """;
        var (ctx, _) = BuildContext("tpl-all", rulesJson: json);
        var svc = new TemplateValidationService(ctx);

        var result = await svc.ValidateAsync("tpl-all",
            new Dictionary<string, string?>
            {
                ["name"] = "John",
                ["code"] = "ABC",
                ["score"] = "42",
            });

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task ValidateAsync_UnknownRuleKind_IsIgnored()
    {
        // "Custom" + an unrecognised kind both fall into the silent-ignore branch.
        const string json = """
            [
                { "fieldName": "x", "ruleKind": "Custom" },
                { "fieldName": "x", "ruleKind": "FooBar", "argument": "irrelevant" }
            ]
            """;
        var (ctx, _) = BuildContext("tpl-unk", rulesJson: json);
        var svc = new TemplateValidationService(ctx);

        var result = await svc.ValidateAsync("tpl-unk",
            new Dictionary<string, string?>());

        result.IsSuccess.Should().BeTrue();
    }
}
