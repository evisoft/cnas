using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.AccessScope;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// R0671 / TOR CF 18.06 — unit tests for <see cref="AccessScopeController"/>. Mirrors
/// the direct-construction pattern used elsewhere in the controller test suite: the
/// service is faked with NSubstitute and the controller is exercised without booting
/// the HTTP pipeline. Authorization (<c>[Authorize]</c>) and rate-limiting attributes
/// are out of scope here — they are validated by the integration-style harness tests.
/// </summary>
public sealed class AccessScopeControllerTests
{
    /// <summary>Single-region allow-list used by the happy-path descriptor.</summary>
    private static readonly string[] ChisRegions = ["CHIS"];

    /// <summary>Shared empty axis (CA1825 — avoid in-place new[] {} allocations).</summary>
    private static readonly string[] EmptyAxis = Array.Empty<string>();

    /// <summary>Builds the SUT around a fresh service substitute.</summary>
    private static AccessScopeController NewController(IAccessScopeService svc) => new(svc);

    /// <summary>
    /// Happy path: the service returns a populated descriptor; the controller emits
    /// 200 OK with the body forwarded verbatim.
    /// </summary>
    [Fact]
    public async Task GetMine_Success_Returns200WithDescriptor()
    {
        var svc = Substitute.For<IAccessScopeService>();
        var descriptor = new AccessScopeDescriptorDto(
            AllowedRegions: ChisRegions,
            AllowedSubdivisionCodes: EmptyAxis,
            AllowedDocumentCategories: EmptyAxis,
            AllowedWorkflowCategories: EmptyAxis,
            IsUnscoped: false);
        svc.GetMineAsync(Arg.Any<CancellationToken>())
           .Returns(Result<AccessScopeDescriptorDto>.Success(descriptor));
        var controller = NewController(svc);

        var result = await controller.GetMineAsync(CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(descriptor);
    }

    /// <summary>
    /// As a national administrator (cnas-tech-admin), the descriptor's IsUnscoped flag
    /// is true and every allow-list is empty. Asserts the end-to-end shape for the
    /// "no scoping" path through the controller.
    /// </summary>
    [Fact]
    public async Task GetMine_AsTechAdmin_ReturnsIsUnscopedTrue()
    {
        var svc = Substitute.For<IAccessScopeService>();
        var descriptor = new AccessScopeDescriptorDto(
            AllowedRegions: EmptyAxis,
            AllowedSubdivisionCodes: EmptyAxis,
            AllowedDocumentCategories: EmptyAxis,
            AllowedWorkflowCategories: EmptyAxis,
            IsUnscoped: true);
        svc.GetMineAsync(Arg.Any<CancellationToken>())
           .Returns(Result<AccessScopeDescriptorDto>.Success(descriptor));
        var controller = NewController(svc);

        var result = await controller.GetMineAsync(CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var dto = ok.Value.Should().BeOfType<AccessScopeDescriptorDto>().Subject;
        dto.IsUnscoped.Should().BeTrue();
        dto.AllowedRegions.Should().BeEmpty();
    }
}
