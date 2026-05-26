using Cnas.Ps.Api.Composition;
using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.Etl;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// R0153 / TOR CF 19.05 — tests for <see cref="EtlProjectionsController"/>.
/// Direct-construction style mirroring the rest of the controller suite; the
/// <see cref="IContributorPeriodProjectionService"/> dependency is faked with
/// NSubstitute.
/// </summary>
public sealed class EtlProjectionsControllerTests
{
    /// <summary>Builds a controller backed by the supplied fakes + a real-shape sqid.</summary>
    private static EtlProjectionsController NewController(
        IContributorPeriodProjectionService svc,
        ISqidService? sqids = null)
    {
        sqids ??= NewSqids();
        return new EtlProjectionsController(svc, sqids);
    }

    /// <summary>Creates a fake sqid service that maps "abc123" -> 42.</summary>
    private static ISqidService NewSqids()
    {
        var sqid = Substitute.For<ISqidService>();
        sqid.Encode(Arg.Any<long>()).Returns(c => $"sqid-{c.Arg<long>()}");
        sqid.TryDecode(Arg.Any<string?>()).Returns(c =>
        {
            var s = c.Arg<string?>();
            if (string.IsNullOrEmpty(s))
            {
                return Result<long>.Failure(ErrorCodes.InvalidSqid, "empty");
            }
            return Result<long>.Success(42L);
        });
        return sqid;
    }

    [Fact]
    public void Controller_HasAuthorizationPolicy()
    {
        var attrs = typeof(EtlProjectionsController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>()
            .ToList();

        attrs.Should().NotBeEmpty("the controller MUST be gated by an explicit Authorize policy");
    }

    [Fact]
    public async Task RunForContributorAsync_HappyPath_Returns200WithRunDto()
    {
        var svc = Substitute.For<IContributorPeriodProjectionService>();
        var dto = new ContributorPeriodProjectionRunDto("sqid-42", 3, 1, 25);
        svc.RebuildForContributorAsync(42L, Arg.Any<CancellationToken>())
            .Returns(Result<ContributorPeriodProjectionRunDto>.Success(dto));
        var controller = NewController(svc);

        var result = await controller.RunForContributorAsync("sqid-42", CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(dto);
    }

    [Fact]
    public async Task RunForContributorAsync_InvalidSqid_Returns400()
    {
        var svc = Substitute.For<IContributorPeriodProjectionService>();
        var sqids = Substitute.For<ISqidService>();
        sqids.TryDecode(Arg.Any<string?>())
            .Returns(Result<long>.Failure(ErrorCodes.InvalidSqid, "bad"));
        var controller = NewController(svc, sqids);

        var result = await controller.RunForContributorAsync("garbage", CancellationToken.None);

        var problem = result.Result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task QueryAsync_HappyPath_Returns200WithDtos()
    {
        var svc = Substitute.For<IContributorPeriodProjectionService>();
        var dto = new ContributorPeriodProjectionDto(
            Id: "sqid-1",
            ContributorSqid: "sqid-42",
            PeriodStartUtc: new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            PeriodEndUtc: new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            CivilStatus: "Single",
            CurrentEmployerCode: "IDNO-1",
            MonthlySalary: 10000m,
            AddressCity: "Chișinău",
            AddressRegion: "MD",
            AddressCountry: "MD",
            PhoneE164: "+37360123456",
            Email: "user@example.md",
            ProjectedAtUtc: new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        svc.QueryAsync(42L, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new[] { dto });
        var controller = NewController(svc);

        var result = await controller.QueryAsync(
            "sqid-42",
            asOfUtc: new(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            cancellationToken: CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeAssignableTo<IEnumerable<ContributorPeriodProjectionDto>>()
            .Which.Should().ContainSingle()
            .Which.AddressCity.Should().Be("Chișinău");
    }

    [Fact]
    public async Task RunAllAsync_HappyPath_Returns200WithBatchSummary()
    {
        var svc = Substitute.For<IContributorPeriodProjectionService>();
        var dto = new ContributorPeriodProjectionRunDto(null, 12, 4, 150);
        svc.RebuildAllAsync(Arg.Any<CancellationToken>())
            .Returns(Result<ContributorPeriodProjectionRunDto>.Success(dto));
        var controller = NewController(svc);

        var result = await controller.RunAllAsync(CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(dto);
    }

    /// <summary>Sanity check on AuthorizationComposition policy strings exist.</summary>
    [Fact]
    public void AuthorizationComposition_ExposesPolicyConstants()
    {
        AuthorizationComposition.CnasUser.Should().NotBeNullOrEmpty();
        AuthorizationComposition.CnasTechAdmin.Should().NotBeNullOrEmpty();
    }
}
