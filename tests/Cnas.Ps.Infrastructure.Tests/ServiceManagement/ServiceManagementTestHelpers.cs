using System;
using System.Collections.Generic;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.ServiceManagement;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.ServiceManagement;

/// <summary>
/// R2501-R2504 / TOR PIR 022-025 — shared test helpers for the
/// service-management quartet test suite. Mirrors the patterns established
/// by the helpdesk + backup test helpers so reviewers can spot-check by
/// analogy.
/// </summary>
internal static class ServiceManagementTestHelpers
{
    /// <summary>Canonical "now" used across the service-management tests.</summary>
    public static readonly DateTime ClockNow = new(2026, 5, 23, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>Default role-set for caller mocks — single <c>cnas-admin</c> role.</summary>
    private static readonly string[] DefaultRoles = ["cnas-admin"];

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
            .UseInMemoryDatabase($"cnas-svcmgmt-{Guid.NewGuid():N}")
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

    /// <summary>Caller-context mock returning sqid USR-1.</summary>
    /// <returns>Configured mock.</returns>
    public static ICallerContext NewCaller()
    {
        var c = Substitute.For<ICallerContext>();
        c.UserId.Returns(1L);
        c.UserSqid.Returns("USR-1");
        c.Roles.Returns(DefaultRoles);
        c.SourceIp.Returns("203.0.113.7");
        c.CorrelationId.Returns("corr-svcmgmt");
        return c;
    }

    /// <summary>Caller-context mock with a configurable user id, sqid, and role set.</summary>
    /// <param name="userId">Numeric user id.</param>
    /// <param name="sqid">User Sqid string.</param>
    /// <param name="roles">Optional roles (defaults to cnas-admin).</param>
    /// <returns>Configured mock.</returns>
    public static ICallerContext NewCallerWith(long userId, string sqid, string[]? roles = null)
    {
        var c = Substitute.For<ICallerContext>();
        c.UserId.Returns(userId);
        c.UserSqid.Returns(sqid);
        c.Roles.Returns(roles ?? DefaultRoles);
        c.SourceIp.Returns("203.0.113.7");
        c.CorrelationId.Returns("corr-svcmgmt");
        return c;
    }

    /// <summary>Seeds a default business-hours policy (Europe/Chisinau, Mon–Fri 08:00–18:00, no holidays).</summary>
    /// <param name="db">Context.</param>
    /// <param name="code">Policy code.</param>
    /// <param name="holidayDatesJson">Optional holidays JSON.</param>
    /// <returns>Persisted entity.</returns>
    public static async Task<BusinessHoursPolicy> SeedDefaultPolicyAsync(
        CnasDbContext db,
        string code = "RM_DEFAULT",
        string? holidayDatesJson = null)
    {
        var policy = new BusinessHoursPolicy
        {
            Code = code,
            DisplayName = $"Test policy {code}",
            OpenTimeLocal = new TimeOnly(8, 0),
            CloseTimeLocal = new TimeOnly(18, 0),
            BusinessDaysMask = 0b0011111,
            TimezoneId = "UTC",
            HolidayDatesJson = holidayDatesJson,
            RegisteredByUserId = 1,
            CreatedAtUtc = ClockNow,
            CreatedBy = "USR-1",
            IsActive = true,
        };
        db.BusinessHoursPolicies.Add(policy);
        await db.SaveChangesAsync().ConfigureAwait(false);
        return policy;
    }

    /// <summary>Constructs the business-hours policy service with defaults.</summary>
    /// <param name="db">Context.</param>
    /// <param name="audit">Audit service.</param>
    /// <param name="now">Optional clock override.</param>
    /// <returns>Service instance.</returns>
    public static BusinessHoursPolicyService NewBusinessHoursService(
        CnasDbContext db, IAuditService audit, DateTime? now = null)
        => new(
            db: db,
            read: db,
            clock: new StubClock(now ?? ClockNow),
            sqids: NewSqidMock(),
            caller: NewCaller(),
            audit: audit,
            createValidator: new BusinessHoursPolicyCreateInputValidator(),
            modifyValidator: new BusinessHoursPolicyModifyInputValidator(),
            filterValidator: new BusinessHoursPolicyFilterValidator());

    /// <summary>Constructs the maintenance-window service with defaults.</summary>
    /// <param name="db">Context.</param>
    /// <param name="audit">Audit service.</param>
    /// <param name="now">Optional clock override.</param>
    /// <returns>Service instance.</returns>
    public static MaintenanceWindowService NewMaintenanceService(
        CnasDbContext db, IAuditService audit, DateTime? now = null)
        => new(
            db: db,
            read: db,
            clock: new StubClock(now ?? ClockNow),
            sqids: NewSqidMock(),
            caller: NewCaller(),
            audit: audit,
            createValidator: new MaintenanceWindowCreateInputValidator(),
            reasonValidator: new MaintenanceWindowReasonInputValidator(),
            filterValidator: new MaintenanceWindowFilterValidator());

    /// <summary>Constructs the system-update schedule service with defaults.</summary>
    /// <param name="db">Context.</param>
    /// <param name="audit">Audit service.</param>
    /// <returns>Service instance.</returns>
    public static SystemUpdateScheduleService NewScheduleService(CnasDbContext db, IAuditService audit)
        => new(
            db: db,
            read: db,
            clock: new StubClock(ClockNow),
            sqids: NewSqidMock(),
            caller: NewCaller(),
            audit: audit,
            createValidator: new SystemUpdateScheduleCreateInputValidator(),
            modifyValidator: new SystemUpdateScheduleModifyInputValidator(),
            filterValidator: new SystemUpdateScheduleFilterValidator());

    /// <summary>Constructs the system-update event service with defaults.</summary>
    /// <param name="db">Context.</param>
    /// <param name="audit">Audit service.</param>
    /// <param name="now">Optional clock override.</param>
    /// <returns>Service instance.</returns>
    public static SystemUpdateEventService NewEventService(
        CnasDbContext db, IAuditService audit, DateTime? now = null)
        => new(
            db: db,
            read: db,
            clock: new StubClock(now ?? ClockNow),
            sqids: NewSqidMock(),
            caller: NewCaller(),
            audit: audit,
            createValidator: new SystemUpdateEventCreateInputValidator(),
            reasonValidator: new SystemUpdateEventReasonInputValidator(),
            filterValidator: new SystemUpdateEventFilterValidator());
}
