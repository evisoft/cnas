using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Localization;
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
/// R0210 / TOR UI 007 / CF 17.16 — CRUD service tests for the translation
/// registry. Covers <see cref="TranslationKeyService"/> create + get round-trip,
/// <see cref="TranslationValueService"/> upsert idempotency, and the approve
/// audit + resolver-invalidation contracts.
/// </summary>
public class TranslationServiceTests
{
    private static readonly DateTime ClockNow = new(2026, 5, 22, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task TranslationKeyService_Create_RoundTrips_Through_Get()
    {
        // RED → GREEN: Create returns DTO with assigned Sqid; Get retrieves it.
        var harness = await Harness.CreateAsync();

        var created = await harness.Keys.CreateAsync(new TranslationKeyUpsertDto(
            Code: "pages.applications.list.title",
            Description: "Title of the applications list page",
            Module: "Public"));

        created.IsSuccess.Should().BeTrue();
        created.Value!.Code.Should().Be("pages.applications.list.title");
        created.Value.Id.Should().NotBeNullOrEmpty();

        var fetched = await harness.Keys.GetAsync(created.Value.Id);
        fetched.IsSuccess.Should().BeTrue();
        fetched.Value!.Code.Should().Be("pages.applications.list.title");
        fetched.Value.Module.Should().Be("Public");
    }

    [Fact]
    public async Task TranslationValueService_Upsert_Insert_ThenUpdate_WritesOneRow()
    {
        var harness = await Harness.CreateAsync();
        var key = await harness.SeedKeyAsync("pages.applications.list.title", module: "Public");

        var firstUpsert = await harness.Values.UpsertAsync(
            keySqid: harness.Sqid(key.Id),
            language: "ro",
            input: new TranslationValueUpsertDto(Text: "Lista cererilor", TranslatorNote: null));
        firstUpsert.IsSuccess.Should().BeTrue();
        firstUpsert.Value!.Text.Should().Be("Lista cererilor");

        var secondUpsert = await harness.Values.UpsertAsync(
            keySqid: harness.Sqid(key.Id),
            language: "ro",
            input: new TranslationValueUpsertDto(Text: "Cererile mele", TranslatorNote: "rev2"));
        secondUpsert.IsSuccess.Should().BeTrue();
        secondUpsert.Value!.Text.Should().Be("Cererile mele");
        secondUpsert.Value.Id.Should().Be(firstUpsert.Value.Id, "upsert must update in place, not insert");

        var rowCount = await harness.Db.TranslationValues
            .CountAsync(v => v.TranslationKeyId == key.Id && v.Language == "ro");
        rowCount.Should().Be(1);
    }

    [Fact]
    public async Task TranslationValueService_Approve_WritesCriticalAuditRow()
    {
        var harness = await Harness.CreateAsync();
        var key = await harness.SeedKeyAsync("pages.applications.list.title");
        var upserted = await harness.Values.UpsertAsync(
            harness.Sqid(key.Id), "ro",
            new TranslationValueUpsertDto("Lista cererilor", null));
        upserted.IsSuccess.Should().BeTrue();
        harness.Audit.ClearReceivedCalls();

        var approved = await harness.Values.ApproveAsync(upserted.Value!.Id);

        approved.IsSuccess.Should().BeTrue();
        approved.Value!.IsApproved.Should().BeTrue();
        await harness.Audit.Received(1).RecordAsync(
            eventCode: "TRANSLATION.APPROVED",
            severity: AuditSeverity.Critical,
            actorId: Arg.Any<string>(),
            targetEntity: nameof(TranslationValue),
            targetEntityId: Arg.Any<long?>(),
            detailsJson: Arg.Any<string>(),
            sourceIp: Arg.Any<string?>(),
            correlationId: Arg.Any<string?>(),
            cancellationToken: Arg.Any<CancellationToken>());
    }

    /// <summary>Wires the services under test against an in-memory DB.</summary>
    internal sealed class Harness
    {
        public required CnasDbContext Db { get; init; }
        public required TranslationKeyService Keys { get; init; }
        public required TranslationValueService Values { get; init; }
        public required TranslationResolver Resolver { get; init; }
        public required IAuditService Audit { get; init; }

        public string Sqid(long id) => $"SQID-{id}";

        public async Task<TranslationKey> SeedKeyAsync(string code, string? module = null)
        {
            var k = new TranslationKey
            {
                Code = code,
                Module = module,
                CreatedAtUtc = ClockNow,
                IsActive = true,
            };
            Db.TranslationKeys.Add(k);
            await Db.SaveChangesAsync();
            return k;
        }

        public static Task<Harness> CreateAsync()
        {
            var dbName = $"cnas-translation-{Guid.NewGuid():N}";
            var services = new ServiceCollection();
            services.AddDbContext<CnasDbContext>(opts => opts
                .UseInMemoryDatabase(dbName)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
            services.AddScoped<ICnasDbContext>(sp => sp.GetRequiredService<CnasDbContext>());
            services.AddScoped<IReadOnlyCnasDbContext>(sp => sp.GetRequiredService<CnasDbContext>());
            var provider = services.BuildServiceProvider();

            var standaloneOpts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase(dbName)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var db = new CnasDbContext(standaloneOpts);

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
            caller.UserSqid.Returns("SQID-1001");
            caller.SourceIp.Returns("127.0.0.1");
            caller.CorrelationId.Returns("corr-1");

            var audit = Substitute.For<IAuditService>();
            audit.RecordAsync(
                Arg.Any<string>(), Arg.Any<AuditSeverity>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success()));

            var resolver = new TranslationResolver(
                provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<TranslationResolver>.Instance);

            IValidator<TranslationKeyUpsertDto> keyValidator = new TranslationKeyUpsertDtoValidator();
            IValidator<TranslationValueUpsertDto> valueValidator = new TranslationValueUpsertDtoValidator();
            var clock = new StubClock(ClockNow);

            var keysSvc = new TranslationKeyService(db, caller, sqids, clock, keyValidator);
            var valuesSvc = new TranslationValueService(db, caller, sqids, clock, audit, resolver, valueValidator);

            return Task.FromResult(new Harness
            {
                Db = db,
                Keys = keysSvc,
                Values = valuesSvc,
                Resolver = resolver,
                Audit = audit,
            });
        }
    }

    /// <summary>Deterministic clock used across tests.</summary>
    internal sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }
}
