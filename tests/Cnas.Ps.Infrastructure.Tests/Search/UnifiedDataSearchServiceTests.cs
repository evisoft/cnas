using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SC = System.Security.Claims;
using Cnas.Ps.Application.Search;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Search;
using Cnas.Ps.Infrastructure.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Search;

/// <summary>
/// R0520 / TOR CF 03.01 — integration tests for
/// <see cref="UnifiedDataSearchService"/>. Validates the cross-entity unified
/// projection across the nine canonical domains via the EF Core InMemory
/// provider.
/// </summary>
public sealed class UnifiedDataSearchServiceTests
{
    /// <summary>Deterministic clock instant for created-at columns.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 24, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>Single-element domain list for the applicants domain (CA1861).</summary>
    private static readonly string[] ApplicantsDomain = new[] { GlobalSearchDomains.Applicants };

    /// <summary>Single-element domain list for the tasks domain (CA1861).</summary>
    private static readonly string[] TasksDomain = new[] { GlobalSearchDomains.Tasks };

    /// <summary>Single-element domain list for the notifications domain (CA1861).</summary>
    private static readonly string[] NotificationsDomain = new[] { GlobalSearchDomains.Notifications };

    /// <summary>Single-element domain list for the applications domain (CA1861).</summary>
    private static readonly string[] ApplicationsDomain = new[] { GlobalSearchDomains.Applications };

    /// <summary>Spawns a fresh InMemory DB context with a unique database name.</summary>
    /// <returns>The wired context.</returns>
    private static CnasDbContext NewContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-unified-search-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    /// <summary>Builds a wired SUT + identity over the supplied context.</summary>
    /// <param name="db">DB context.</param>
    /// <returns>The SUT bundle.</returns>
    private static (UnifiedDataSearchService Service, SC.ClaimsPrincipal Admin) Build(CnasDbContext db)
    {
        var sqids = Substitute.For<ISqidService>();
        sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");
        var filter = new AbacSearchRowLevelFilter(db);
        var service = new UnifiedDataSearchService(db, sqids, filter);
        var admin = new SC.ClaimsPrincipal(new SC.ClaimsIdentity(
            new[] { new SC.Claim(SC.ClaimTypes.Role, "cnas-admin") },
            authenticationType: "test"));
        return (service, admin);
    }

    // ─────────────────────── applicants domain ───────────────────────

    /// <summary>Seeded Solicitant rows project into UnifiedSearchHitDto.</summary>
    [Fact]
    public async Task SearchAsync_Applicants_ProjectsSolicitantHits()
    {
        using var db = NewContext();
        db.Solicitants.Add(new Solicitant
        {
            NationalId = "1003600012347",
            NationalIdHash = IdHashHelper.Hash("Alpha citizen"),
            Kind = ApplicantKind.NaturalPerson,
            DisplayName = "Alpha citizen",
            CreatedAtUtc = ClockNow,
            IsActive = true,
        });
        await db.SaveChangesAsync();
        var (service, admin) = Build(db);

        var result = await service.SearchAsync(
            new UnifiedSearchInput("alpha", ApplicantsDomain, Skip: 0, Take: 20),
            admin,
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalHits.Should().Be(1);
        var hit = result.Value.Results.Single();
        hit.Domain.Should().Be(GlobalSearchDomains.Applicants);
        hit.Title.Should().Be("Alpha citizen");
        hit.Url.Should().StartWith("/applicants/");
        hit.Highlights.Should().NotBeNull();
    }

    // ─────────────────────── tasks domain ───────────────────────

    /// <summary>Seeded WorkflowTask rows project into UnifiedSearchHitDto.</summary>
    [Fact]
    public async Task SearchAsync_Tasks_ProjectsWorkflowTaskHits()
    {
        using var db = NewContext();
        db.WorkflowTasks.Add(new WorkflowTask
        {
            DossierId = 1L,
            Title = "Verify ALPHA documents",
            Status = WorkflowTaskStatus.Pending,
            CreatedAtUtc = ClockNow,
            IsActive = true,
        });
        await db.SaveChangesAsync();
        var (service, admin) = Build(db);

        var result = await service.SearchAsync(
            new UnifiedSearchInput("alpha", TasksDomain, Skip: 0, Take: 20),
            admin,
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Results.Should().ContainSingle()
            .Which.Title.Should().Be("Verify ALPHA documents");
    }

    // ─────────────────────── notifications domain ───────────────────────

    /// <summary>Seeded Notification rows project into UnifiedSearchHitDto.</summary>
    [Fact]
    public async Task SearchAsync_Notifications_ProjectsHits()
    {
        using var db = NewContext();
        db.Notifications.Add(new Notification
        {
            RecipientUserId = 1L,
            Channel = NotificationChannel.Email,
            Subject = "Alpha approval pending",
            Body = "Your application is under review.",
            CreatedAtUtc = ClockNow,
            IsActive = true,
        });
        await db.SaveChangesAsync();
        var (service, admin) = Build(db);

        var result = await service.SearchAsync(
            new UnifiedSearchInput("alpha", NotificationsDomain, Skip: 0, Take: 20),
            admin,
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var hit = result.Value.Results.Should().ContainSingle().Subject;
        hit.Domain.Should().Be(GlobalSearchDomains.Notifications);
        hit.Title.Should().Contain("Alpha");
    }

    // ─────────────────────── applications domain — uniform shape ───────────────────────

    /// <summary>Applications hit carries the unified shape including non-null Url + Highlights.</summary>
    [Fact]
    public async Task SearchAsync_Applications_ReturnsUniformShape()
    {
        using var db = NewContext();
        db.Applications.Add(new ServiceApplication
        {
            SolicitantId = 1,
            ServicePassportId = 1,
            Status = ApplicationStatus.Submitted,
            FormPayloadJson = "{}",
            ReferenceNumber = "REF-ALPHA-001",
            CreatedAtUtc = ClockNow,
            IsActive = true,
        });
        await db.SaveChangesAsync();
        var (service, admin) = Build(db);

        var result = await service.SearchAsync(
            new UnifiedSearchInput("alpha", ApplicationsDomain, Skip: 0, Take: 20),
            admin,
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var hit = result.Value.Results.Should().ContainSingle().Subject;
        hit.Url.Should().NotBeNullOrEmpty();
        hit.Url.Should().StartWith("/applications/");
        hit.Highlights.Should().NotBeNull();
        hit.RelevanceScore.Should().BeGreaterThan(0);
        hit.Sqid.Should().StartWith("SQID-");
    }

    // ─────────────────────── empty domains expands to all 9 ───────────────────────

    /// <summary>Null/empty domain filter expands to the nine canonical unified domains.</summary>
    [Fact]
    public async Task SearchAsync_EmptyDomains_FansOutAcrossAllUnifiedDomains()
    {
        using var db = NewContext();
        db.Solicitants.Add(new Solicitant
        {
            NationalId = "1003600012347",
            NationalIdHash = IdHashHelper.Hash("Alpha citizen"),
            Kind = ApplicantKind.NaturalPerson,
            DisplayName = "Alpha citizen",
            CreatedAtUtc = ClockNow,
            IsActive = true,
        });
        db.WorkflowTasks.Add(new WorkflowTask
        {
            DossierId = 1L,
            Title = "ALPHA verify",
            Status = WorkflowTaskStatus.Pending,
            CreatedAtUtc = ClockNow,
            IsActive = true,
        });
        await db.SaveChangesAsync();
        var (service, admin) = Build(db);

        var result = await service.SearchAsync(
            new UnifiedSearchInput("alpha", Domains: null, Skip: 0, Take: 20),
            admin,
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var domains = result.Value.Results.Select(r => r.Domain).Distinct().ToList();
        domains.Should().Contain(GlobalSearchDomains.Applicants);
        domains.Should().Contain(GlobalSearchDomains.Tasks);
    }
}
