using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Dashboard;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.Dashboard;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Cnas.Ps.Infrastructure.Tests.Dashboard;

/// <summary>
/// R0536 / TOR CF 04.09 — unit tests for the three user-scoped KPI producers that
/// expose "my requests in examination", "my requests completed in the rolling
/// window", and "my requests by status" tiles on the citizen dashboard. Each
/// producer must:
/// <list type="bullet">
///   <item>scope to the Solicitant identified by <c>ICallerContext.UserId</c>;</item>
///   <item>emit at least one <see cref="KpiWidget"/> with a non-empty
///         <see cref="KpiWidget.DeepLinkUrl"/> pointing at the citizen "my
///         applications" page;</item>
///   <item>return zero (and not throw) on empty data.</item>
/// </list>
/// </summary>
public sealed class MyRequestsKpiProducerTests
{
    /// <summary>Deterministic UTC clock — pinned for reproducible window math.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 24, 12, 0, 0, DateTimeKind.Utc);

    private sealed class StubClock : ICnasTimeProvider
    {
        public DateTime UtcNow => ClockNow;
    }

    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-my-requests-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    private static ServiceApplication BuildApp(
        long solicitantId,
        ApplicationStatus status,
        DateTime? closedAtUtc = null,
        bool active = true) => new()
        {
            ServicePassportId = 1L,
            SolicitantId = solicitantId,
            ReferenceNumber = $"REF-{Guid.NewGuid():N}".Substring(0, 16),
            Status = status,
            ClosedAtUtc = closedAtUtc,
            CreatedAtUtc = ClockNow.AddDays(-2),
            IsActive = active,
        };

    // ─────────── MyRequestsInExaminationKpiProducer ───────────

    /// <summary>
    /// The "in examination" tile counts applications owned by the caller whose status
    /// is <see cref="ApplicationStatus.UnderExamination"/>. Other statuses MUST NOT
    /// contribute; applications owned by a different Solicitant MUST NOT contribute.
    /// </summary>
    [Fact]
    public async Task MyRequestsInExamination_CountsOnlyUnderExaminationOwnedByCaller()
    {
        const long callerId = 11L;
        await using var db = CreateContext();
        db.Applications.AddRange(
            BuildApp(callerId, ApplicationStatus.UnderExamination),
            BuildApp(callerId, ApplicationStatus.UnderExamination),
            // Different status — must not count.
            BuildApp(callerId, ApplicationStatus.Submitted),
            // Different solicitant — must not count.
            BuildApp(solicitantId: 99L, ApplicationStatus.UnderExamination));
        await db.SaveChangesAsync();
        IReadOnlyCnasDbContext read = db;
        var producer = new MyRequestsInExaminationKpiProducer(read);

        var result = await producer.ProduceAsync(callerId);

        result.IsSuccess.Should().BeTrue();
        var widget = result.Value!.Single();
        widget.Code.Should().Be(MyRequestsInExaminationKpiProducer.WidgetCode);
        widget.Value.Should().Be(2m);
        widget.DeepLinkUrl.Should().NotBeNullOrWhiteSpace();
        widget.Category.Should().Be(nameof(DashboardCategory.WorkflowUpdates));
    }

    /// <summary>
    /// With no in-examination applications, the producer returns a zero-value widget
    /// (never throws, never null).
    /// </summary>
    [Fact]
    public async Task MyRequestsInExamination_NoMatches_ReturnsZero()
    {
        const long callerId = 11L;
        await using var db = CreateContext();
        IReadOnlyCnasDbContext read = db;
        var producer = new MyRequestsInExaminationKpiProducer(read);

        var result = await producer.ProduceAsync(callerId);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Single().Value.Should().Be(0m);
    }

    // ─────────── MyRequestsCompletedInWindowKpiProducer ───────────

    /// <summary>
    /// The "completed in window" tile counts applications owned by the caller closed
    /// within the rolling window (default 30 days). Older closures and closures by
    /// other Solicitants MUST NOT contribute. Open applications MUST NOT contribute
    /// either (no <see cref="ServiceApplication.ClosedAtUtc"/> stamp).
    /// </summary>
    [Fact]
    public async Task MyRequestsCompletedInWindow_CountsClosedRowsInsideRollingWindow()
    {
        const long callerId = 11L;
        await using var db = CreateContext();
        db.Applications.AddRange(
            // In-window closures owned by caller.
            BuildApp(callerId, ApplicationStatus.Closed, closedAtUtc: ClockNow.AddDays(-5)),
            BuildApp(callerId, ApplicationStatus.Closed, closedAtUtc: ClockNow.AddDays(-29)),
            // Out-of-window closure (older than 30 days).
            BuildApp(callerId, ApplicationStatus.Closed, closedAtUtc: ClockNow.AddDays(-31)),
            // Different solicitant — must not count.
            BuildApp(solicitantId: 99L, ApplicationStatus.Closed, closedAtUtc: ClockNow.AddDays(-1)),
            // Open application — must not count.
            BuildApp(callerId, ApplicationStatus.UnderExamination));
        await db.SaveChangesAsync();
        IReadOnlyCnasDbContext read = db;
        var producer = new MyRequestsCompletedInWindowKpiProducer(read, new StubClock());

        var result = await producer.ProduceAsync(callerId);

        result.IsSuccess.Should().BeTrue();
        var widget = result.Value!.Single();
        widget.Code.Should().Be(MyRequestsCompletedInWindowKpiProducer.WidgetCode);
        widget.Value.Should().Be(2m);
        widget.DeepLinkUrl.Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// The producer returns a zero-value widget when there are no in-window closures.
    /// </summary>
    [Fact]
    public async Task MyRequestsCompletedInWindow_NoMatches_ReturnsZero()
    {
        const long callerId = 11L;
        await using var db = CreateContext();
        IReadOnlyCnasDbContext read = db;
        var producer = new MyRequestsCompletedInWindowKpiProducer(read, new StubClock());

        var result = await producer.ProduceAsync(callerId);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Single().Value.Should().Be(0m);
    }

    // ─────────── MyRequestsByStatusKpiProducer ───────────

    /// <summary>
    /// The "by status" tile emits one <see cref="KpiWidget"/> per
    /// <see cref="ApplicationStatus"/> bucket the caller has at least one application
    /// in. Each widget's <see cref="KpiWidget.Code"/> embeds the status name; the
    /// <see cref="KpiWidget.Value"/> is the count of caller-owned applications in
    /// that status. Other Solicitants' applications MUST NOT contribute.
    /// </summary>
    [Fact]
    public async Task MyRequestsByStatus_EmitsOneWidgetPerStatusBucketOwnedByCaller()
    {
        const long callerId = 11L;
        await using var db = CreateContext();
        db.Applications.AddRange(
            BuildApp(callerId, ApplicationStatus.UnderExamination),
            BuildApp(callerId, ApplicationStatus.UnderExamination),
            BuildApp(callerId, ApplicationStatus.Submitted),
            BuildApp(callerId, ApplicationStatus.Closed, closedAtUtc: ClockNow.AddDays(-1)),
            // Different solicitant — must not contribute.
            BuildApp(solicitantId: 99L, ApplicationStatus.UnderExamination));
        await db.SaveChangesAsync();
        IReadOnlyCnasDbContext read = db;
        var producer = new MyRequestsByStatusKpiProducer(read);

        var result = await producer.ProduceAsync(callerId);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Should().HaveCount(3,
            "caller has applications in exactly three status buckets");

        var byStatus = result.Value!.ToDictionary(w => w.Code, w => w.Value);
        byStatus.Should().ContainKey($"{MyRequestsByStatusKpiProducer.WidgetCodePrefix}_UNDEREXAMINATION");
        byStatus[$"{MyRequestsByStatusKpiProducer.WidgetCodePrefix}_UNDEREXAMINATION"].Should().Be(2m);
        byStatus.Should().ContainKey($"{MyRequestsByStatusKpiProducer.WidgetCodePrefix}_SUBMITTED");
        byStatus[$"{MyRequestsByStatusKpiProducer.WidgetCodePrefix}_SUBMITTED"].Should().Be(1m);
        byStatus.Should().ContainKey($"{MyRequestsByStatusKpiProducer.WidgetCodePrefix}_CLOSED");
        byStatus[$"{MyRequestsByStatusKpiProducer.WidgetCodePrefix}_CLOSED"].Should().Be(1m);

        result.Value!.Should().OnlyContain(w => !string.IsNullOrWhiteSpace(w.DeepLinkUrl),
            "every histogram bucket carries a deep-link to /profile/me/applications");
    }

    /// <summary>
    /// The "by status" tile returns an empty list (no widgets) when the caller has
    /// no applications — never throws, never null.
    /// </summary>
    [Fact]
    public async Task MyRequestsByStatus_NoApplications_ReturnsEmptyList()
    {
        const long callerId = 11L;
        await using var db = CreateContext();
        IReadOnlyCnasDbContext read = db;
        var producer = new MyRequestsByStatusKpiProducer(read);

        var result = await producer.ProduceAsync(callerId);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Should().BeEmpty();
    }

    // ─────────── declarative producer metadata ───────────

    /// <summary>
    /// Each producer must declare its canonical CF 04.02 category and the
    /// wildcard role allow-list — they are user-scoped tiles that the every
    /// authenticated caller should see for their own data.
    /// </summary>
    [Fact]
    public void Producers_DeclareCategoryAndWildcardRoles()
    {
        // Use throwaway in-memory DBs so the constructors don't blow up.
        var db = CreateContext();
        IReadOnlyCnasDbContext read = db;

        var examined = new MyRequestsInExaminationKpiProducer(read);
        var completed = new MyRequestsCompletedInWindowKpiProducer(read, new StubClock());
        var byStatus = new MyRequestsByStatusKpiProducer(read);

        examined.Category.Should().Be(DashboardCategory.WorkflowUpdates);
        examined.SupportedRoles.Should().Contain("*");
        completed.Category.Should().Be(DashboardCategory.WorkflowUpdates);
        completed.SupportedRoles.Should().Contain("*");
        byStatus.Category.Should().Be(DashboardCategory.WorkflowUpdates);
        byStatus.SupportedRoles.Should().Contain("*");
    }
}
