using Cnas.Ps.Application.Notifications;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Common;
using Cnas.Ps.Infrastructure.Notifications;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.Tests.Notifications;

/// <summary>
/// R0172 / TOR CF 22.05 — tests for <see cref="NotificationDeepLinkResolver"/>.
/// One test per supported entity type proves the route round-trip (Sqid
/// encoded id, case-insensitive type match) and one test pins the unknown-
/// type fallback to <c>null</c>.
/// </summary>
/// <remarks>
/// Tests are written BEFORE the production code per CLAUDE.md RULE 1. The
/// Sqid encoder is exercised directly so the assertions can match on the
/// exact encoded suffix rather than a regex.
/// </remarks>
public sealed class NotificationDeepLinkResolverTests
{
    /// <summary>Stable Sqid alphabet used in every test in this file.</summary>
    private const string Alphabet =
        "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    /// <summary>Sqid minimum length used in every test in this file.</summary>
    private const int MinLength = 6;

    /// <summary>Convenience factory that builds the SUT + a real Sqid encoder.</summary>
    private static (NotificationDeepLinkResolver Sut, ISqidService Sqids) NewSut()
    {
        var sqids = new SqidService(Options.Create(new SqidOptions
        {
            Alphabet = Alphabet,
            MinLength = MinLength,
        }));
        return (new NotificationDeepLinkResolver(sqids), sqids);
    }

    [Fact]
    public void Resolve_Application_ReturnsApplicationsRoute()
    {
        var (sut, sqids) = NewSut();
        const long entityId = 4523L;

        var route = sut.Resolve(NotificationRelatedEntityTypes.Application, entityId);

        route.Should().Be($"/applications/{sqids.Encode(entityId)}");
    }

    [Fact]
    public void Resolve_Contributor_ReturnsContributorsRoute()
    {
        var (sut, sqids) = NewSut();
        const long entityId = 42L;

        var route = sut.Resolve(NotificationRelatedEntityTypes.Contributor, entityId);

        route.Should().Be($"/contributors/{sqids.Encode(entityId)}");
    }

    [Fact]
    public void Resolve_InsuredPerson_ReturnsInsuredPersonsRoute()
    {
        var (sut, sqids) = NewSut();
        const long entityId = 9001L;

        var route = sut.Resolve(NotificationRelatedEntityTypes.InsuredPerson, entityId);

        route.Should().Be($"/insured-persons/{sqids.Encode(entityId)}");
    }

    [Fact]
    public void Resolve_Dossier_ReturnsDossiersRoute()
    {
        var (sut, sqids) = NewSut();
        const long entityId = 7777L;

        var route = sut.Resolve(NotificationRelatedEntityTypes.Dossier, entityId);

        route.Should().Be($"/dossiers/{sqids.Encode(entityId)}");
    }

    [Fact]
    public void Resolve_WorkflowTask_ReturnsTasksRoute()
    {
        var (sut, sqids) = NewSut();
        const long entityId = 31337L;

        var route = sut.Resolve(NotificationRelatedEntityTypes.WorkflowTask, entityId);

        route.Should().Be($"/tasks/{sqids.Encode(entityId)}");
    }

    [Fact]
    public void Resolve_ReportRun_ReturnsReportRunsRoute()
    {
        var (sut, sqids) = NewSut();
        const long entityId = 100L;

        var route = sut.Resolve(NotificationRelatedEntityTypes.ReportRun, entityId);

        route.Should().Be($"/reports/runs/{sqids.Encode(entityId)}");
    }

    [Fact]
    public void Resolve_UnknownType_ReturnsNull()
    {
        var (sut, _) = NewSut();

        var route = sut.Resolve("AlienEntity", 42L);

        route.Should().BeNull();
    }

    [Fact]
    public void Resolve_NullType_ReturnsNull()
    {
        var (sut, _) = NewSut();

        sut.Resolve(null, 42L).Should().BeNull();
        sut.Resolve("", 42L).Should().BeNull();
        sut.Resolve("   ", 42L).Should().BeNull();
    }

    [Fact]
    public void Resolve_NullId_ReturnsNull()
    {
        var (sut, _) = NewSut();

        sut.Resolve(NotificationRelatedEntityTypes.Application, null).Should().BeNull();
        sut.Resolve(NotificationRelatedEntityTypes.Application, 0L).Should().BeNull();
        sut.Resolve(NotificationRelatedEntityTypes.Application, -1L).Should().BeNull();
    }

    [Fact]
    public void Resolve_TypeCaseInsensitive()
    {
        var (sut, sqids) = NewSut();
        const long entityId = 5L;
        var encoded = sqids.Encode(entityId);

        sut.Resolve("application", entityId).Should().Be($"/applications/{encoded}");
        sut.Resolve("APPLICATION", entityId).Should().Be($"/applications/{encoded}");
        sut.Resolve("Application", entityId).Should().Be($"/applications/{encoded}");
    }
}
