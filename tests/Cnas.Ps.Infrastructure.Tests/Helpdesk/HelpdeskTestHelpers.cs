using System;
using System.Collections.Generic;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.Helpdesk;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Helpdesk;

/// <summary>
/// R2500 / TOR PIR 020-023 — shared helpers for the helpdesk test suite.
/// Mirrors the BackupTestHelpers pattern so reviewers can spot-check by
/// analogy.
/// </summary>
internal static class HelpdeskTestHelpers
{
    /// <summary>Canonical "now" used across the helpdesk tests.</summary>
    public static readonly DateTime ClockNow = new(2026, 5, 23, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Test-only fixed UTC clock.</summary>
    public sealed class StubClock : ICnasTimeProvider
    {
        /// <summary>Constructs the clock.</summary>
        /// <param name="now">Instant returned from <see cref="UtcNow"/>.</param>
        public StubClock(DateTime now) { UtcNow = now; }

        /// <inheritdoc />
        public DateTime UtcNow { get; }
    }

    /// <summary>Builds a fresh EF Core InMemory context backed by a unique store.</summary>
    /// <returns>A new context.</returns>
    public static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-helpdesk-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    /// <summary>Returns a Sqid mock that round-trips "SQID-{id}".</summary>
    /// <returns>Configured mock.</returns>
    public static ISqidService NewSqidMock()
    {
        var s = Substitute.For<ISqidService>();
        s.Encode(Arg.Any<long>()).Returns(c => $"SQID-{c.Arg<long>()}");
        s.TryDecode(Arg.Any<string>()).Returns(c =>
        {
            var v = c.Arg<string>();
            if (v is not null && v.StartsWith("SQID-", StringComparison.Ordinal)
                && long.TryParse(v["SQID-".Length..], out var id))
            {
                return Result<long>.Success(id);
            }
            return Result<long>.Failure(ErrorCodes.InvalidSqid, "bad sqid");
        });
        return s;
    }

    /// <summary>Audit mock that captures every event code written.</summary>
    /// <param name="codes">Out parameter — captured codes list.</param>
    /// <returns>Configured mock.</returns>
    public static IAuditService NewAuditCapturing(out List<string> codes)
    {
        var list = new List<string>();
        codes = list;
        var a = Substitute.For<IAuditService>();
        a.RecordAsync(
                Arg.Any<string>(), Arg.Any<AuditSeverity>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(c => { list.Add(c.ArgAt<string>(0)); return Task.FromResult(Result.Success()); });
        return a;
    }

    /// <summary>Caller-context mock returning the supplied user id (default 1).</summary>
    /// <param name="userId">User id to return; default 1.</param>
    /// <returns>Configured mock.</returns>
    public static ICallerContext NewCaller(long userId = 1L)
    {
        var c = Substitute.For<ICallerContext>();
        c.UserId.Returns(userId);
        c.UserSqid.Returns($"SQID-{userId}");
        c.SourceIp.Returns("203.0.113.7");
        c.CorrelationId.Returns("corr-help");
        return c;
    }

    /// <summary>Seeds a helpdesk category onto the context.</summary>
    /// <param name="db">EF Core context.</param>
    /// <param name="code">Category code (default AUTH).</param>
    /// <param name="firstResponseMinutes">First-response SLA minutes (default 60).</param>
    /// <param name="resolutionMinutes">Resolution SLA minutes (default 480).</param>
    /// <param name="isActive">IsActive flag.</param>
    /// <returns>Persisted category.</returns>
    public static async Task<SupportTicketCategory> SeedCategoryAsync(
        CnasDbContext db,
        string code = "AUTH",
        int firstResponseMinutes = 60,
        int resolutionMinutes = 480,
        bool isActive = true)
    {
        var cat = new SupportTicketCategory
        {
            Code = code,
            DisplayName = $"Test {code}",
            Description = null,
            DefaultSeverity = SupportTicketSeverity.Normal,
            FirstResponseSlaMinutes = firstResponseMinutes,
            ResolutionSlaMinutes = resolutionMinutes,
            EscalationQueueCode = "L2_GENERAL",
            RegisteredByUserId = 1,
            CreatedAtUtc = ClockNow,
            CreatedBy = "SQID-1",
            IsActive = isActive,
        };
        db.SupportTicketCategories.Add(cat);
        await db.SaveChangesAsync().ConfigureAwait(false);
        return cat;
    }

    /// <summary>Builds the category service with sensible defaults.</summary>
    /// <param name="db">Context.</param>
    /// <param name="audit">Audit service.</param>
    /// <param name="caller">Optional caller override.</param>
    /// <returns>Service instance.</returns>
    public static SupportTicketCategoryService NewCategoryService(
        CnasDbContext db,
        IAuditService audit,
        ICallerContext? caller = null)
        => new(
            db: db,
            read: db,
            clock: new StubClock(ClockNow),
            sqids: NewSqidMock(),
            caller: caller ?? NewCaller(),
            audit: audit,
            createValidator: new SupportTicketCategoryCreateInputValidator(),
            modifyValidator: new SupportTicketCategoryModifyInputValidator(),
            filterValidator: new SupportTicketCategoryFilterValidator());

    /// <summary>Builds the ticket service with sensible defaults.</summary>
    /// <param name="db">Context.</param>
    /// <param name="audit">Audit service.</param>
    /// <param name="clock">Optional clock override (defaults to <see cref="ClockNow"/>).</param>
    /// <param name="caller">Optional caller override.</param>
    /// <returns>Service instance.</returns>
    public static SupportTicketService NewTicketService(
        CnasDbContext db,
        IAuditService audit,
        ICnasTimeProvider? clock = null,
        ICallerContext? caller = null)
        => new(
            db: db,
            read: db,
            clock: clock ?? new StubClock(ClockNow),
            sqids: NewSqidMock(),
            caller: caller ?? NewCaller(),
            audit: audit,
            submitValidator: new SupportTicketSubmitInputValidator(),
            assignValidator: new SupportTicketAssignInputValidator(),
            resolutionValidator: new SupportTicketResolutionInputValidator(),
            reasonValidator: new SupportTicketReasonInputValidator(),
            commentValidator: new SupportTicketCommentInputValidator(),
            filterValidator: new SupportTicketFilterValidator());

    /// <summary>Builds the SLA evaluator with sensible defaults.</summary>
    /// <param name="db">Context.</param>
    /// <param name="audit">Audit service.</param>
    /// <param name="clock">Clock override.</param>
    /// <returns>Evaluator instance.</returns>
    public static SupportTicketSlaEvaluator NewEvaluator(
        CnasDbContext db,
        IAuditService audit,
        ICnasTimeProvider clock)
        => new(
            db: db,
            clock: clock,
            sqids: NewSqidMock(),
            caller: NewCaller(),
            audit: audit,
            logger: NullLogger<SupportTicketSlaEvaluator>.Instance);
}
