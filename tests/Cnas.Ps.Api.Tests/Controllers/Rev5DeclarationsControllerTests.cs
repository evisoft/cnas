using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.Rev5;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// R0910 — controller-level tests for <see cref="Rev5DeclarationsController"/>.
/// </summary>
public sealed class Rev5DeclarationsControllerTests
{
    /// <summary>Sample input DTO for the register endpoint.</summary>
    private static Rev5DeclarationRegisterInputDto SampleInput() => new(
        FilingContributorSqid: "SQID-1",
        ReportingMonth: new DateOnly(2026, 4, 1),
        ReferenceNumber: "REV5-001",
        Rows: [new Rev5DeclarationRowInputDto("HASH-1", 1_000m, 290m)]);

    /// <summary>Sample output DTO returned by the service mock.</summary>
    private static Rev5DeclarationDto SampleOutput() => new(
        Id: "REV5-1",
        FilingContributorSqid: "SQID-1",
        ReportingMonth: new DateOnly(2026, 4, 1),
        FiledAtUtc: new DateTime(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc),
        ReferenceNumber: "REV5-001",
        Status: "Received",
        TotalDeclaredAmount: 290m,
        RowCount: 1,
        UnmatchedRowCount: 0,
        UnmatchedNationalIdHashPrefixes: [],
        Notes: null);

    /// <summary>Sqid stub that round-trips "SQID-{id}" strings.</summary>
    private static ISqidService NewSqidStub()
    {
        var sqids = Substitute.For<ISqidService>();
        sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");
        sqids.TryDecode(Arg.Any<string>()).Returns(call =>
        {
            var s = call.Arg<string>();
            if (s is not null && s.StartsWith("SQID-", StringComparison.Ordinal)
                && long.TryParse(s["SQID-".Length..], out var id))
            {
                return Result<long>.Success(id);
            }
            return Result<long>.Failure(ErrorCodes.InvalidSqid, "bad sqid");
        });
        return sqids;
    }

    /// <summary>R0910 — POST /api/rev5-declarations returns 201 with the Sqid id on success.</summary>
    [Fact]
    public async Task RegisterAsync_ServiceReturnsSuccess_Returns201()
    {
        var svc = Substitute.For<IRev5DeclarationService>();
        svc.RegisterAsync(Arg.Any<Rev5DeclarationRegisterInputDto>(), Arg.Any<CancellationToken>())
            .Returns(Result<Rev5DeclarationDto>.Success(SampleOutput()));
        var controller = new Rev5DeclarationsController(svc, NewSqidStub());

        var result = await controller.RegisterAsync(SampleInput(), CancellationToken.None);

        var created = result.Result.Should().BeOfType<CreatedAtActionResult>().Subject;
        var dto = created.Value.Should().BeOfType<Rev5DeclarationDto>().Subject;
        dto.Id.Should().Be("REV5-1");
        dto.Status.Should().Be("Received");
    }
}
