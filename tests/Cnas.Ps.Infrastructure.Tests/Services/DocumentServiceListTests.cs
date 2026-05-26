using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.QueryBudget;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.AccessScope;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Qbe;
using Cnas.Ps.Infrastructure.QueryBudget;
using Cnas.Ps.Infrastructure.Services;
using Cnas.Ps.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// R0671 continuation — service-level tests for
/// <see cref="DocumentServiceImpl.ListAsync"/>. Wires the real budget guard, QBE
/// converter, and access-scope filter against an InMemory DB so the canonical
/// vertical-slice pipeline is exercised end-to-end.
/// </summary>
public sealed class DocumentServiceListTests
{
    /// <summary>Document allow-list narrowing the registry to Attachments only.</summary>
    private static readonly string[] AttachmentOnly = ["Attachment"];

    /// <summary>Empty array shared between scope-builder calls so CA1825 stays quiet.</summary>
    private static readonly string[] EmptyAxis = Array.Empty<string>();

    /// <summary>Default caller roles wired by <see cref="Build"/>.</summary>
    private static readonly string[] DefaultRoles = ["cnas-user"];

    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-docs-list-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    /// <summary>Builds the SUT against a real budget service + InMemory DB + caller stub.</summary>
    private static (DocumentServiceImpl Svc, CnasDbContext Db) Build(
        IAccessScope? scope = null,
        int? budgetOverride = null,
        string[]? roles = null)
    {
        var db = CreateContext();
        var sqids = Substitute.For<ISqidService>();
        sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");
        sqids.TryDecode(Arg.Any<string?>()).Returns(call =>
        {
            var raw = call.Arg<string?>();
            if (raw is not null && raw.StartsWith("SQID-", StringComparison.Ordinal)
                                && long.TryParse(raw[5..], out var id))
            {
                return Result<long>.Success(id);
            }

            return Result<long>.Failure(ErrorCodes.InvalidSqid, "Invalid Sqid.");
        });

        IQueryBudgetPolicy policy = budgetOverride is { } b
            ? new SingleBudgetPolicy(b)
            : new StaticQueryBudgetPolicy(NullLogger<StaticQueryBudgetPolicy>.Instance);
        var budget = new QueryBudgetService(policy, NullLogger<QueryBudgetService>.Instance);
        var qbeConverter = new QbeToLinqConverter(new QbeRegistrySchemaProvider());
        var accessFilter = new AccessScopeFilter();

        var caller = Substitute.For<ICallerContext>();
        caller.AccessScope.Returns(scope ?? RolesBasedAccessScope.Unscoped);
        caller.UserId.Returns(1L);
        caller.UserSqid.Returns("SQID-1");
        caller.Roles.Returns(roles ?? DefaultRoles);

        var storage = Substitute.For<IFileStorage>();
        var storageOptions = Options.Create(new MinioOptions
        {
            CitizenUploadsBucket = "citizen-uploads",
        });
        var clock = Substitute.For<ICnasTimeProvider>();
        clock.UtcNow.Returns(new DateTime(2026, 5, 23, 10, 0, 0, DateTimeKind.Utc));

        var svc = new DocumentServiceImpl(
            db, sqids, storage, storageOptions, clock, caller, budget, qbeConverter, accessFilter);
        return (svc, db);
    }

    /// <summary>Seeds a mix of Document kinds + dates so filter tests can assert narrowing.</summary>
    private static async Task SeedAsync(CnasDbContext db, int rowCount)
    {
        for (int i = 0; i < rowCount; i++)
        {
            db.Documents.Add(new Document
            {
                CreatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(i),
                Kind = i % 2 == 0 ? DocumentKind.Attachment : DocumentKind.Decision,
                Title = $"file-{i:D5}.pdf",
                MimeType = "application/pdf",
                SizeBytes = 1024 + i,
                StorageObjectKey = $"key-{i}",
                StorageBucket = "test",
                ContentSha256Hex = "deadbeef",
                IsActive = true,
            });
        }
        await db.SaveChangesAsync();
    }

    /// <summary>Happy path: ListAsync returns a paged Sqid-encoded projection.</summary>
    [Fact]
    public async Task ListAsync_ReturnsPagedResultWithSqidIds()
    {
        var (svc, db) = Build();
        await SeedAsync(db, 6);

        var result = await svc.ListAsync(new DocumentsListInput(Take: 50));

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(6);
        result.Value.Items.Should().OnlyContain(r => r.Id.StartsWith("SQID-", StringComparison.Ordinal));
        result.Value.TotalCount.Should().Be(6);
    }

    /// <summary>QBE narrowing on Kind reduces the result-set to a single category.</summary>
    [Fact]
    public async Task ListAsync_RespectsQbeFilterOnKind()
    {
        var (svc, db) = Build();
        await SeedAsync(db, 10);

        var qbe = new QbeFilterDto("AND", new[]
        {
            new QbeConditionDto("Kind", "Equals", "Decision"),
        });
        var result = await svc.ListAsync(new DocumentsListInput(Filter: qbe, Take: 50));

        result.IsSuccess.Should().BeTrue();
        // 10 seeded; 5 even rows = Attachment, 5 odd rows = Decision.
        result.Value.Items.Should().HaveCount(5);
        result.Value.Items.Should().OnlyContain(d => d.DocumentKind == "Decision");
    }

    /// <summary>Date-range filter narrows by CreatedAtUtc.</summary>
    [Fact]
    public async Task ListAsync_RespectsDateRangeFilter()
    {
        var (svc, db) = Build(roles: ["solicitant"]);
        await SeedAsync(db, 10);

        // Rows have CreatedAtUtc = 2026-01-01 + i days. From inclusive 2026-01-03,
        // to exclusive 2026-01-06 → days 3,4,5 → 3 rows.
        var from = new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 1, 6, 0, 0, 0, DateTimeKind.Utc);
        var result = await svc.ListAsync(new DocumentsListInput(FromUtc: from, ToUtc: to, Take: 50));

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(3);
    }

    /// <summary>Above-cap Take is rejected by the validator with ValidationFailed.</summary>
    [Fact]
    public async Task ListAsync_RejectsAboveCapTake_WithValidationFailed()
    {
        var (svc, db) = Build();
        await SeedAsync(db, 1);

        var result = await svc.ListAsync(new DocumentsListInput(Take: 500));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
    }

    /// <summary>Tight budget + many rows + no filter → QueryTooBroad.</summary>
    [Fact]
    public async Task ListAsync_BudgetGate_RefusesOverBudgetCall()
    {
        var (svc, db) = Build(budgetOverride: 10);
        await SeedAsync(db, 50);

        var result = await svc.ListAsync(new DocumentsListInput(Take: 50));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.QueryTooBroad);
        svc.LastBudgetVerdict.Should().NotBeNull();
        svc.LastBudgetVerdict!.Allowed.Should().BeFalse();
    }

    /// <summary>AccessScope with AllowedDocumentCategories narrows the result-set BEFORE budget.</summary>
    [Fact]
    public async Task ListAsync_RespectsAccessScopeDocumentCategoryFilter()
    {
        // Scope = only Attachment kind. Seed 10 rows; 5 Attachment + 5 Decision → 5 visible.
        var scope = new RolesBasedAccessScope(EmptyAxis, EmptyAxis, AttachmentOnly, EmptyAxis);
        var (svc, db) = Build(scope: scope);
        await SeedAsync(db, 10);

        var result = await svc.ListAsync(new DocumentsListInput(Take: 50));

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(5);
        result.Value.Items.Should().OnlyContain(d => d.DocumentKind == "Attachment");
    }

    [Fact]
    public async Task GetDownloadUrlAsync_CitizenCaller_DeniesOwnerlessDocument()
    {
        var (svc, db) = Build(roles: ["solicitant"]);
        db.Documents.Add(new Document
        {
            Id = 42,
            CreatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Kind = DocumentKind.Attachment,
            Title = "orphan.pdf",
            MimeType = "application/pdf",
            SizeBytes = 1024,
            StorageObjectKey = "key-orphan",
            StorageBucket = "test",
            ContentSha256Hex = "deadbeef",
            IsActive = true,
            DossierId = null,
        });
        await db.SaveChangesAsync();

        var result = await svc.GetDownloadUrlAsync("SQID-42");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.Forbidden);
    }

    /// <summary>Test stub that returns a single-budget policy regardless of registry.</summary>
    private sealed class SingleBudgetPolicy(int budget) : IQueryBudgetPolicy
    {
        /// <inheritdoc />
        public QueryBudgetPolicy GetForRegistry(string registry) =>
            new(registry, budget, Array.Empty<RefinementHintRule>());
    }
}
