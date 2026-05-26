using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.MLog;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// R0116 + R0195 — pins the MLog mirror filter behaviour of
/// <see cref="MLogCategoryFilter"/>. The audit drainer consults this seam
/// before deciding whether to forward an event to MLog; the three behaviours
/// pinned here together close R0195's "admin-config category filter" gap.
/// </summary>
public sealed class AuditServiceMLogCategoryFilterTests
{
    /// <summary>
    /// Builds a fresh DI scope-factory backed by an in-memory db so the
    /// filter can pull its snapshot through its standard
    /// <see cref="IServiceScopeFactory"/> seam.
    /// </summary>
    private static (IServiceScopeFactory Scopes, CnasDbContext Db) NewHarness()
    {
        var dbName = $"cnas-mlog-filter-{Guid.NewGuid():N}";
        var services = new ServiceCollection();
        services.AddDbContext<CnasDbContext>(opts => opts
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        services.AddScoped<IReadOnlyCnasDbContext>(sp => sp.GetRequiredService<CnasDbContext>());
        var provider = services.BuildServiceProvider();
        var scopes = provider.GetRequiredService<IServiceScopeFactory>();
        var db = provider.GetRequiredService<CnasDbContext>();
        return (scopes, db);
    }

    private static MLogCategoryFilter NewFilter(IServiceScopeFactory scopes) =>
        new(scopes, NullLogger<MLogCategoryFilter>.Instance);

    /// <summary>Enabled category + severity ≥ floor → mirrored.</summary>
    [Fact]
    public void ShouldMirror_EnabledAndSeverityAboveFloor_ReturnsTrue()
    {
        var (scopes, db) = NewHarness();
        db.MLogCategoryConfigs.Add(new MLogCategoryConfig
        {
            CreatedAtUtc = DateTime.UtcNow,
            CategoryCode = "APPLICATION.RECEIVE",
            DisplayName = "App receive",
            IsEnabled = true,
            MinSeverity = MLogSeverityFloor.Notice,
            IsActive = true,
        });
        db.SaveChanges();

        var filter = NewFilter(scopes);

        filter.ShouldMirror("APPLICATION.RECEIVE.SUBMITTED", AuditSeverity.Notice)
            .Should().BeTrue();
    }

    /// <summary>Disabled category → never mirrored.</summary>
    [Fact]
    public void ShouldMirror_DisabledCategory_ReturnsFalse()
    {
        var (scopes, db) = NewHarness();
        db.MLogCategoryConfigs.Add(new MLogCategoryConfig
        {
            CreatedAtUtc = DateTime.UtcNow,
            CategoryCode = "AUTH",
            DisplayName = "Auth",
            IsEnabled = false,
            MinSeverity = MLogSeverityFloor.Notice,
            IsActive = true,
        });
        db.SaveChanges();

        var filter = NewFilter(scopes);

        filter.ShouldMirror("AUTH.LOGIN.FAIL", AuditSeverity.Critical)
            .Should().BeFalse();
    }

    /// <summary>Severity below floor → skipped.</summary>
    [Fact]
    public void ShouldMirror_SeverityBelowFloor_ReturnsFalse()
    {
        var (scopes, db) = NewHarness();
        db.MLogCategoryConfigs.Add(new MLogCategoryConfig
        {
            CreatedAtUtc = DateTime.UtcNow,
            CategoryCode = "REPORT_ACCESS",
            DisplayName = "Report access",
            IsEnabled = true,
            MinSeverity = MLogSeverityFloor.Critical,
            IsActive = true,
        });
        db.SaveChanges();

        var filter = NewFilter(scopes);

        filter.ShouldMirror("REPORT_ACCESS.LIST", AuditSeverity.Notice)
            .Should().BeFalse();
    }

    /// <summary>No matching row + non-Critical severity → falls back to Critical-only default and skips.</summary>
    [Fact]
    public void ShouldMirror_NoMatchingRow_NonCritical_Skipped()
    {
        var (scopes, db) = NewHarness();
        // Empty registry — should fall back to "Critical only" mirror.
        var filter = NewFilter(scopes);

        filter.ShouldMirror("ARBITRARY.EVENT", AuditSeverity.Notice).Should().BeFalse();
        filter.ShouldMirror("ARBITRARY.EVENT", AuditSeverity.Critical).Should().BeTrue();
    }
}
