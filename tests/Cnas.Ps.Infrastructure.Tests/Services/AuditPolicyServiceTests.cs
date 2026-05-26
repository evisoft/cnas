using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// R0182 / SEC 042 — CRUD service tests for <see cref="AuditPolicyService"/>.
/// Each test exercises a single behaviour: create + audit emission, update flow,
/// disable flow, validation refusal, and the cache-invalidation side effect.
/// </summary>
public class AuditPolicyServiceTests
{
    private static readonly DateTime ClockNow = new(2026, 5, 21, 10, 0, 0, DateTimeKind.Utc);

    private static readonly IReadOnlyList<string> EmptyRedactKeys = Array.Empty<string>();
    private static readonly IReadOnlyList<string> IbanRedactKeys = new[] { "iban" };

    private static AuditPolicyCreateInput ValidCreate(string code = "solicitant.view.search") => new(
        Code: code,
        Module: "Solicitant",
        Screen: "Search",
        DataCategory: null,
        EventCodePattern: "^SOLICITANT\\.VIEW\\.SEARCH$",
        OverrideSeverity: null,
        SuppressAudit: false,
        ExtraRedactKeys: EmptyRedactKeys,
        Priority: 100,
        IsEnabled: true,
        Description: "test");

    [Fact]
    public async Task Create_Writes_Critical_Audit_Row()
    {
        var harness = Harness.Create();

        var result = await harness.Service.CreateAsync(ValidCreate());

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().StartWith("SQID-");

        // Audit emission — Critical event code AUDIT.POLICY.CREATED, actor=caller sqid.
        await harness.Audit.Received(1).RecordAsync(
            eventCode: "AUDIT.POLICY.CREATED",
            severity: AuditSeverity.Critical,
            actorId: Arg.Any<string>(),
            targetEntity: nameof(AuditPolicy),
            targetEntityId: Arg.Any<long?>(),
            detailsJson: Arg.Any<string>(),
            sourceIp: Arg.Any<string?>(),
            correlationId: Arg.Any<string?>(),
            cancellationToken: Arg.Any<CancellationToken>());

        var row = await harness.Db.AuditPolicies.SingleAsync();
        row.Code.Should().Be("solicitant.view.search");
        row.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task Update_Writes_Critical_Audit_Row()
    {
        var harness = Harness.Create();
        var created = await harness.Service.CreateAsync(ValidCreate());
        created.IsSuccess.Should().BeTrue();
        harness.Audit.ClearReceivedCalls();

        var update = new AuditPolicyUpdateInput(
            Module: "Solicitant",
            Screen: "Detail",
            DataCategory: "PII",
            EventCodePattern: "^SOLICITANT\\.VIEW\\.DETAIL$",
            OverrideSeverity: "Sensitive",
            SuppressAudit: false,
            ExtraRedactKeys: IbanRedactKeys,
            Priority: 50,
            IsEnabled: true,
            Description: "updated");

        var result = await harness.Service.UpdateAsync(created.Value, update);

        result.IsSuccess.Should().BeTrue();
        await harness.Audit.Received(1).RecordAsync(
            eventCode: "AUDIT.POLICY.UPDATED",
            severity: AuditSeverity.Critical,
            actorId: Arg.Any<string>(),
            targetEntity: nameof(AuditPolicy),
            targetEntityId: Arg.Any<long?>(),
            detailsJson: Arg.Any<string>(),
            sourceIp: Arg.Any<string?>(),
            correlationId: Arg.Any<string?>(),
            cancellationToken: Arg.Any<CancellationToken>());

        var row = await harness.Db.AuditPolicies.SingleAsync();
        row.Module.Should().Be("Solicitant");
        row.Screen.Should().Be("Detail");
        row.DataCategory.Should().Be("PII");
        row.OverrideSeverity.Should().Be(AuditSeverity.Sensitive);
        row.ExtraRedactKeys.Should().BeEquivalentTo(IbanRedactKeys);
    }

    [Fact]
    public async Task Disable_Writes_Critical_Audit_Row_And_Flips_Flags()
    {
        var harness = Harness.Create();
        var created = await harness.Service.CreateAsync(ValidCreate());
        created.IsSuccess.Should().BeTrue();
        harness.Audit.ClearReceivedCalls();

        var result = await harness.Service.DisableAsync(created.Value);

        result.IsSuccess.Should().BeTrue();
        await harness.Audit.Received(1).RecordAsync(
            eventCode: "AUDIT.POLICY.DISABLED",
            severity: AuditSeverity.Critical,
            actorId: Arg.Any<string>(),
            targetEntity: nameof(AuditPolicy),
            targetEntityId: Arg.Any<long?>(),
            detailsJson: Arg.Any<string>(),
            sourceIp: Arg.Any<string?>(),
            correlationId: Arg.Any<string?>(),
            cancellationToken: Arg.Any<CancellationToken>());

        // Note: soft-deleted rows are filtered out by IsActive — must include the
        // archived row by querying without the IsActive filter.
        var row = await harness.Db.AuditPolicies.IgnoreQueryFilters().SingleAsync();
        row.IsEnabled.Should().BeFalse();
        row.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task Create_DuplicateCode_ReturnsConflict()
    {
        var harness = Harness.Create();
        (await harness.Service.CreateAsync(ValidCreate())).IsSuccess.Should().BeTrue();

        var dup = await harness.Service.CreateAsync(ValidCreate());

        dup.IsFailure.Should().BeTrue();
        dup.ErrorCode.Should().Be(ErrorCodes.Conflict);
    }

    [Fact]
    public async Task Create_InvalidCode_ReturnsValidationFailed()
    {
        var harness = Harness.Create();

        var result = await harness.Service.CreateAsync(ValidCreate("Bad.Code.Uppercase"));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task Create_TriggersResolverInvalidation()
    {
        // Arrange — fresh harness; resolver is empty initially.
        var harness = Harness.Create();
        harness.Resolver.SnapshotCount.Should().Be(0);

        // Act
        await harness.Service.CreateAsync(ValidCreate());

        // Assert — resolver snapshot picked up the new row.
        harness.Resolver.SnapshotCount.Should().Be(1);
    }

    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-audit-policy-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }

    private sealed class Harness
    {
        public required CnasDbContext Db { get; init; }
        public required AuditPolicyService Service { get; init; }
        public required AuditPolicyResolver Resolver { get; init; }
        public required IAuditService Audit { get; init; }

        public static Harness Create()
        {
            // Shared in-memory store name so every scope sees the same data.
            var dbName = $"cnas-audit-policy-{Guid.NewGuid():N}";

            // Provider whose scoped Db is built per-scope against the same shared
            // in-memory backing store. This avoids the disposed-context issue when
            // the resolver's scope ends but the service still wants the same data.
            var services = new ServiceCollection();
            services.AddDbContext<CnasDbContext>(opts => opts
                .UseInMemoryDatabase(dbName)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
            services.AddScoped<ICnasDbContext>(sp => sp.GetRequiredService<CnasDbContext>());
            services.AddScoped<IReadOnlyCnasDbContext>(sp => sp.GetRequiredService<CnasDbContext>());
            var provider = services.BuildServiceProvider();

            // Build the service context against an independent options object that
            // shares the in-memory store via the same database name. This context
            // is owned by the test (never touched by the resolver's scope dispose).
            var standaloneOptions = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase(dbName)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var db = new CnasDbContext(standaloneOptions);

            var sqids = Substitute.For<ISqidService>();
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

            var caller = Substitute.For<ICallerContext>();
            caller.UserId.Returns(1001L);
            caller.UserSqid.Returns("SQID-CALLER");
            caller.Roles.Returns(["cnas-tech-admin"]);
            caller.SourceIp.Returns("127.0.0.1");
            caller.CorrelationId.Returns("corr-1");

            var audit = Substitute.For<IAuditService>();
            audit.RecordAsync(
                Arg.Any<string>(), Arg.Any<AuditSeverity>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success()));

            var resolver = new AuditPolicyResolver(
                provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<AuditPolicyResolver>.Instance);

            IValidator<AuditPolicyCreateInput> createValidator = new AuditPolicyInputValidator();
            IValidator<AuditPolicyUpdateInput> updateValidator = new AuditPolicyUpdateInputValidator();

            var service = new AuditPolicyService(
                db, caller, sqids, new StubClock(ClockNow), audit, resolver, createValidator, updateValidator);

            return new Harness
            {
                Db = db,
                Service = service,
                Resolver = resolver,
                Audit = audit,
            };
        }
    }
}
