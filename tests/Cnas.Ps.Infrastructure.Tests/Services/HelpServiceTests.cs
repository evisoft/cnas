using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Help;
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
/// R0225 / TOR UI 015 — CRUD service tests for the contextual-help registry.
/// Covers <see cref="HelpTopicService"/> create + get round-trip,
/// <see cref="HelpTopicTranslationService"/> upsert + approve, and the
/// <see cref="HelpResolver"/> known-code / missing-language contracts.
/// </summary>
public class HelpServiceTests
{
    private static readonly DateTime ClockNow = new(2026, 5, 22, 10, 0, 0, DateTimeKind.Utc);
    private static readonly string[] ExpectedRoEnLanguages = new[] { "ro", "en" };

    [Fact]
    public async Task HelpTopicService_Create_RoundTrips_Through_Get()
    {
        var harness = await Harness.CreateAsync();

        var created = await harness.Topics.CreateAsync(new HelpTopicUpsertDto(
            Code: "pages.applications.new.applicant-section",
            Module: "Public",
            AnchorSelector: "#applicant-section"));
        created.IsSuccess.Should().BeTrue();
        created.Value!.Code.Should().Be("pages.applications.new.applicant-section");

        var fetched = await harness.Topics.GetAsync(created.Value.Id);
        fetched.IsSuccess.Should().BeTrue();
        fetched.Value!.Module.Should().Be("Public");
        fetched.Value.AnchorSelector.Should().Be("#applicant-section");
    }

    [Fact]
    public async Task HelpTopicTranslationService_Upsert_ThenApprove_FlipsFlagAndAudits()
    {
        var harness = await Harness.CreateAsync();
        var topic = await harness.SeedTopicAsync("pages.x", "Public");

        var upserted = await harness.Translations.UpsertAsync(
            harness.Sqid(topic.Id), "ro",
            new HelpTopicTranslationUpsertDto(
                Title: "Despre solicitant",
                BodyMarkdown: "# Despre\n\nSecțiune.",
                TranslatorNote: null));
        upserted.IsSuccess.Should().BeTrue();
        upserted.Value!.IsApproved.Should().BeFalse();
        harness.Audit.ClearReceivedCalls();

        var approved = await harness.Translations.ApproveAsync(upserted.Value.Id);
        approved.IsSuccess.Should().BeTrue();
        approved.Value!.IsApproved.Should().BeTrue();
        await harness.Audit.Received(1).RecordAsync(
            eventCode: "HELP.APPROVED",
            severity: AuditSeverity.Critical,
            actorId: Arg.Any<string>(),
            targetEntity: nameof(HelpTopicTranslation),
            targetEntityId: Arg.Any<long?>(),
            detailsJson: Arg.Any<string>(),
            sourceIp: Arg.Any<string?>(),
            correlationId: Arg.Any<string?>(),
            cancellationToken: Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HelpResolver_KnownCode_ReturnsTopicAndTranslations()
    {
        var harness = await Harness.CreateAsync();
        var topic = await harness.SeedTopicAsync("pages.x", "Public");
        await harness.SeedTranslationAsync(topic.Id, "ro", "RO title", "RO body");
        await harness.SeedTranslationAsync(topic.Id, "en", "EN title", "EN body");
        await harness.Resolver.InvalidateAsync();

        var fetched = await harness.Resolver.GetByCodeAsync("pages.x", "ro");

        fetched.Should().NotBeNull();
        fetched!.Code.Should().Be("pages.x");
        fetched.Translations.Should().HaveCount(2);
        fetched.Translations.Select(t => t.Language).Should().BeEquivalentTo(ExpectedRoEnLanguages);
    }

    [Fact]
    public async Task HelpResolver_UnknownCode_ReturnsNull()
    {
        // Caller picks the UI fallback when the resolver returns null.
        var harness = await Harness.CreateAsync();
        await harness.Resolver.InvalidateAsync();

        var fetched = await harness.Resolver.GetByCodeAsync("pages.does-not-exist", "ro");

        fetched.Should().BeNull();
    }

    /// <summary>Harness wiring services + resolver against a shared in-memory DB.</summary>
    internal sealed class Harness
    {
        public required CnasDbContext Db { get; init; }
        public required HelpTopicService Topics { get; init; }
        public required HelpTopicTranslationService Translations { get; init; }
        public required HelpResolver Resolver { get; init; }
        public required IAuditService Audit { get; init; }

        public string Sqid(long id) => $"SQID-{id}";

        public async Task<HelpTopic> SeedTopicAsync(string code, string module)
        {
            var t = new HelpTopic
            {
                Code = code,
                Module = module,
                CreatedAtUtc = ClockNow,
                IsActive = true,
            };
            Db.HelpTopics.Add(t);
            await Db.SaveChangesAsync();
            return t;
        }

        public async Task SeedTranslationAsync(long topicId, string language, string title, string body)
        {
            Db.HelpTopicTranslations.Add(new HelpTopicTranslation
            {
                HelpTopicId = topicId,
                Language = language,
                Title = title,
                BodyMarkdown = body,
                IsApproved = true,
                CreatedAtUtc = ClockNow,
                IsActive = true,
            });
            await Db.SaveChangesAsync();
        }

        public static Task<Harness> CreateAsync()
        {
            var dbName = $"cnas-help-{Guid.NewGuid():N}";
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

            var resolver = new HelpResolver(
                provider.GetRequiredService<IServiceScopeFactory>(),
                sqids,
                NullLogger<HelpResolver>.Instance);

            IValidator<HelpTopicUpsertDto> topicValidator = new HelpTopicUpsertDtoValidator();
            IValidator<HelpTopicTranslationUpsertDto> trValidator = new HelpTopicTranslationUpsertDtoValidator();
            var clock = new TranslationServiceTests.StubClock(ClockNow);

            var topicsSvc = new HelpTopicService(db, caller, sqids, clock, resolver, topicValidator);
            var trsSvc = new HelpTopicTranslationService(db, caller, sqids, clock, audit, resolver, trValidator);

            return Task.FromResult(new Harness
            {
                Db = db,
                Topics = topicsSvc,
                Translations = trsSvc,
                Resolver = resolver,
                Audit = audit,
            });
        }
    }
}
