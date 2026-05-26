using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Qbe;
using Cnas.Ps.Application.QueryBudget;
using Cnas.Ps.Application.Reports;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Qbe;
using Cnas.Ps.Infrastructure.Services.Reports;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Reports.Adhoc;

/// <summary>
/// R0156 / TOR CF 09.02 / FLEX 003 — service-level tests for
/// <see cref="ReportTemplateService"/>. Uses EF Core InMemory + NSubstitute, mirroring
/// the harness shape used by <c>SavedSearchServiceTests</c>.
/// </summary>
public class ReportTemplateServiceTests
{
    /// <summary>Deterministic clock anchor for all tests.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 22, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>Reused seeds extracted to satisfy CA1861 (no inline new[] in default args).</summary>
    private static readonly string[] DefaultSelectedFields = ["Id", "DisplayName", "Kind"];

    /// <summary>Reused ordering seed.</summary>
    private static readonly ReportOrderingDto[] DefaultOrdering =
        [new ReportOrderingDto("DisplayName", ReportOrderingDto.Asc)];

    /// <summary>Stealth-update payload seed used by the non-owner test.</summary>
    private static readonly string[] StealthSelectedFields = ["Id"];

    /// <summary>Owner roles seed (passed to NSubstitute).</summary>
    private static readonly string[] OwnerRoles = ["cnas-user"];

    /// <summary>Expected accessible-codes set for the list-mix test.</summary>
    private static readonly string[] ExpectedAccessibleCodes =
        ["report.own.private", "report.own.shared", "report.other.shared"];

    /// <summary>Field list for the schema-rejection test (contains a fake field).</summary>
    private static readonly string[] FieldsWithUnknown = ["Id", "NotARealField"];

    /// <summary>Field list for the group-by-not-in-selected test.</summary>
    private static readonly string[] FieldsWithoutKind = ["Id", "DisplayName"];

    /// <summary>Helper for building a valid create payload — the bare minimum to pass validation.</summary>
    private static ReportTemplateCreateDto NewValidInput(
        string code = "report.solicitants.basic",
        bool isShared = false,
        string? groupBy = null,
        IReadOnlyList<string>? selectedFields = null,
        IReadOnlyList<ReportOrderingDto>? ordering = null) =>
        new(
            Code: code,
            Name: "Basic solicitants",
            Description: "Active solicitants",
            Registry: QueryBudgetRegistries.Solicitant,
            SelectedFields: selectedFields ?? DefaultSelectedFields,
            Filter: new QbeFilterDto(QbeFilter.CombinatorAnd, Array.Empty<QbeConditionDto>()),
            Ordering: ordering ?? DefaultOrdering,
            GroupByField: groupBy,
            IsShared: isShared);

    [Fact]
    public async Task Create_ValidInput_PersistsRow_AndAssignsOwnerToCaller()
    {
        var harness = Harness.Create();

        var result = await harness.Service.CreateAsync(NewValidInput());

        result.IsSuccess.Should().BeTrue();
        var row = await harness.Db.ReportTemplates.SingleAsync();
        row.OwnerUserId.Should().Be(Harness.OwnerUserId);
        row.Code.Should().Be("report.solicitants.basic");
        row.IsActive.Should().BeTrue();
        row.CreatedAtUtc.Should().Be(ClockNow);
    }

    [Fact]
    public async Task Update_AsNonOwner_ReturnsForbidden()
    {
        var harness = Harness.Create();
        var create = await harness.Service.CreateAsync(NewValidInput(isShared: true));
        create.IsSuccess.Should().BeTrue();

        // Take the freshly-persisted row id so the non-owner harness can target it.
        var rowId = (await harness.Db.ReportTemplates.SingleAsync()).Id;

        var otherHarness = harness.WithCaller(Harness.OtherUserId, "SQID-OTHER");
        var update = await otherHarness.Service.UpdateAsync(rowId, new ReportTemplateUpdateDto(
            Name: "stealth-rename",
            Description: null,
            SelectedFields: StealthSelectedFields,
            Filter: new QbeFilterDto(QbeFilter.CombinatorAnd, Array.Empty<QbeConditionDto>()),
            Ordering: Array.Empty<ReportOrderingDto>(),
            GroupByField: null,
            IsShared: false));

        update.IsFailure.Should().BeTrue();
        update.ErrorCode.Should().Be(ErrorCodes.Forbidden);
    }

    [Fact]
    public async Task Delete_AsOwner_SoftDeletesRow_AndEmitsAudit()
    {
        var harness = Harness.Create();
        var create = await harness.Service.CreateAsync(NewValidInput());
        create.IsSuccess.Should().BeTrue();
        var rowId = (await harness.Db.ReportTemplates.SingleAsync()).Id;

        var del = await harness.Service.DeleteAsync(rowId);

        del.IsSuccess.Should().BeTrue();
        var row = await harness.Db.ReportTemplates.IgnoreQueryFilters().SingleAsync();
        row.IsActive.Should().BeFalse();
        await harness.Audit.Received().RecordAsync(
            "REPORT_TEMPLATE.DELETED",
            Arg.Any<AuditSeverity>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<long?>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListAccessible_ReturnsOwnedAndShared_NotOtherUsersPrivate()
    {
        var harness = Harness.Create();
        (await harness.Service.CreateAsync(NewValidInput(code: "report.own.private", isShared: false))).IsSuccess.Should().BeTrue();
        (await harness.Service.CreateAsync(NewValidInput(code: "report.own.shared", isShared: true))).IsSuccess.Should().BeTrue();

        var otherHarness = harness.WithCaller(Harness.OtherUserId, "SQID-OTHER");
        (await otherHarness.Service.CreateAsync(NewValidInput(code: "report.other.shared", isShared: true))).IsSuccess.Should().BeTrue();
        (await otherHarness.Service.CreateAsync(NewValidInput(code: "report.other.private", isShared: false))).IsSuccess.Should().BeTrue();

        var list = await harness.Service.ListAccessibleAsync();

        list.Select(i => i.Code).Should().BeEquivalentTo(ExpectedAccessibleCodes);
    }

    [Fact]
    public async Task Create_DuplicateCode_ReturnsConflict()
    {
        var harness = Harness.Create();
        var first = await harness.Service.CreateAsync(NewValidInput(code: "report.x"));
        first.IsSuccess.Should().BeTrue();

        var dup = await harness.Service.CreateAsync(NewValidInput(code: "report.x"));

        dup.IsFailure.Should().BeTrue();
        dup.ErrorCode.Should().Be(ErrorCodes.Conflict);
    }

    [Fact]
    public async Task Create_FieldNotInRegistrySchema_ReturnsQbeFieldNotQueryable()
    {
        var harness = Harness.Create();

        var result = await harness.Service.CreateAsync(NewValidInput(
            selectedFields: FieldsWithUnknown));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.QbeFieldNotQueryable);
    }

    [Fact]
    public async Task Create_GroupByNotInSelectedFields_ReturnsValidationFailed_FromService()
    {
        // Schema-aware check fires inside the service when the wire validator hasn't.
        // GroupByField references a real field in the registry schema but it is not
        // one of the selected fields.
        var harness = Harness.Create();

        var result = await harness.Service.CreateAsync(NewValidInput(
            selectedFields: FieldsWithoutKind,
            groupBy: "Kind"));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
    }

    // ─────────────────────── Harness ───────────────────────

    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }

    private sealed class Harness
    {
        public const long OwnerUserId = 9001L;
        public const long OtherUserId = 9002L;

        public required CnasDbContext Db { get; init; }
        public required ReportTemplateService Service { get; init; }
        public required ISqidService Sqids { get; init; }
        public required IAuditService Audit { get; init; }

        public static Harness Create()
        {
            var db = CreateContext();
            return Build(db, OwnerUserId, "SQID-OWNER");
        }

        public Harness WithCaller(long userId, string userSqid) =>
            Build(Db, userId, userSqid, Sqids, Audit);

        private static CnasDbContext CreateContext()
        {
            var opts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-reporttpl-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            return new CnasDbContext(opts);
        }

        private static Harness Build(
            CnasDbContext db,
            long callerUserId,
            string callerSqid,
            ISqidService? sharedSqids = null,
            IAuditService? sharedAudit = null)
        {
            var sqids = sharedSqids ?? Substitute.For<ISqidService>();
            if (sharedSqids is null)
            {
                sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");
                sqids.TryDecode(Arg.Any<string?>()).Returns(call =>
                {
                    var arg = call.Arg<string?>();
                    if (!string.IsNullOrEmpty(arg)
                        && arg.StartsWith("SQID-", StringComparison.Ordinal)
                        && long.TryParse(arg.AsSpan(5), out var n))
                    {
                        return Result<long>.Success(n);
                    }
                    return Result<long>.Failure(ErrorCodes.InvalidSqid, "bad sqid");
                });
            }

            var caller = Substitute.For<ICallerContext>();
            caller.UserId.Returns(callerUserId);
            caller.UserSqid.Returns(callerSqid);
            caller.Roles.Returns(OwnerRoles);
            caller.SourceIp.Returns("127.0.0.1");
            caller.CorrelationId.Returns($"corr-{callerUserId}");

            var audit = sharedAudit ?? Substitute.For<IAuditService>();
            if (sharedAudit is null)
            {
                audit.RecordAsync(
                    Arg.Any<string>(), Arg.Any<AuditSeverity>(), Arg.Any<string>(),
                    Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
                    Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(Result.Success()));
            }

            var schemas = new QbeRegistrySchemaProvider();
            var clock = new StubClock(ClockNow);
            var service = new ReportTemplateService(db, caller, sqids, clock, audit, schemas);
            return new Harness { Db = db, Service = service, Sqids = sqids, Audit = audit };
        }
    }
}
