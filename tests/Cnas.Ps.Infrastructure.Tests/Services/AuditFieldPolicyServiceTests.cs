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
/// R0183 / SEC 043 — CRUD service tests for <see cref="AuditFieldPolicyService"/>.
/// Mirrors the R0182 audit-policy service test pattern.
/// </summary>
public class AuditFieldPolicyServiceTests
{
    private static readonly DateTime ClockNow = new(2026, 5, 21, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>Reused tracked-fields seed to satisfy CA1861 (no new[] inside record ctor).</summary>
    private static readonly string[] DefaultTracked = { "DisplayName", "Email" };

    /// <summary>Reused suppressed-fields seed to satisfy CA1861.</summary>
    private static readonly string[] DefaultSuppressed = { "NationalId" };

    /// <summary>Reused caller roles to satisfy CA1861.</summary>
    private static readonly string[] CallerRoles = { "cnas-tech-admin" };

    private static AuditFieldPolicyCreateInput ValidCreate(string entityType = "Solicitant") => new(
        EntityType: entityType,
        TrackedFields: DefaultTracked,
        SuppressedFields: DefaultSuppressed,
        Severity: "Sensitive",
        RequireAnyChange: true,
        IsEnabled: true,
        Description: "test");

    [Fact]
    public async Task Create_Writes_Critical_Audit_Row()
    {
        var harness = Harness.Create();

        var result = await harness.Service.CreateAsync(ValidCreate());

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().StartWith("SQID-");

        await harness.Audit.Received(1).RecordAsync(
            eventCode: "AUDIT.FIELDPOLICY.CREATED",
            severity: AuditSeverity.Critical,
            actorId: Arg.Any<string>(),
            targetEntity: nameof(AuditFieldPolicy),
            targetEntityId: Arg.Any<long?>(),
            detailsJson: Arg.Any<string>(),
            sourceIp: Arg.Any<string?>(),
            correlationId: Arg.Any<string?>(),
            cancellationToken: Arg.Any<CancellationToken>());

        var row = await harness.Db.AuditFieldPolicies.SingleAsync();
        row.EntityType.Should().Be("Solicitant");
        row.IsActive.Should().BeTrue();
        row.TrackedFields.Should().Contain("DisplayName");
        row.SuppressedFields.Should().Contain("NationalId");
    }

    [Fact]
    public async Task Create_TriggersResolverInvalidation()
    {
        var harness = Harness.Create();
        harness.Resolver.SnapshotCount.Should().Be(0);

        await harness.Service.CreateAsync(ValidCreate());

        harness.Resolver.SnapshotCount.Should().Be(1);
    }

    [Fact]
    public async Task Create_DuplicateEntityType_ReturnsConflict()
    {
        var harness = Harness.Create();
        (await harness.Service.CreateAsync(ValidCreate())).IsSuccess.Should().BeTrue();

        var dup = await harness.Service.CreateAsync(ValidCreate());

        dup.IsFailure.Should().BeTrue();
        dup.ErrorCode.Should().Be(ErrorCodes.Conflict);
    }

    private static CnasDbContext CreateContext(string dbName)
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase(dbName)
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
        public required AuditFieldPolicyService Service { get; init; }
        public required AuditFieldPolicyResolver Resolver { get; init; }
        public required IAuditService Audit { get; init; }

        public static Harness Create()
        {
            var dbName = $"cnas-fieldpolicy-svc-{Guid.NewGuid():N}";

            var services = new ServiceCollection();
            services.AddDbContext<CnasDbContext>(opts => opts
                .UseInMemoryDatabase(dbName)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
            services.AddScoped<ICnasDbContext>(sp => sp.GetRequiredService<CnasDbContext>());
            services.AddScoped<IReadOnlyCnasDbContext>(sp => sp.GetRequiredService<CnasDbContext>());
            var provider = services.BuildServiceProvider();

            var db = CreateContext(dbName);

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
            caller.Roles.Returns(CallerRoles);
            caller.SourceIp.Returns("127.0.0.1");
            caller.CorrelationId.Returns("corr-1");

            var audit = Substitute.For<IAuditService>();
            audit.RecordAsync(
                Arg.Any<string>(), Arg.Any<AuditSeverity>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success()));

            var resolver = new AuditFieldPolicyResolver(
                provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<AuditFieldPolicyResolver>.Instance);

            IValidator<AuditFieldPolicyCreateInput> createValidator = new AuditFieldPolicyInputValidator();
            IValidator<AuditFieldPolicyUpdateInput> updateValidator = new AuditFieldPolicyUpdateInputValidator();

            var service = new AuditFieldPolicyService(
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
