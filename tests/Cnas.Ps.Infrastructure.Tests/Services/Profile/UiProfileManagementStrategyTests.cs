using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Profile;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Services.Profile;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services.Profile;

/// <summary>
/// R0622 / TOR CF 13.03 — tests for
/// <see cref="UiProfileManagementStrategy"/>. The UI channel delegates
/// to <c>IProfileService.UpdateMyContactAsync</c> then re-reads through
/// <c>GetMineAsync</c>; the tests pin both legs of the contract.
/// </summary>
public sealed class UiProfileManagementStrategyTests
{
    /// <summary>Canonical post-mutation projection returned by the stubbed GET.</summary>
    private static readonly ProfileOutput PostUpdate = new(
        "userSqid",
        "Citizen Renamed",
        "renamed@example.md",
        "+37360123456",
        "ro",
        Array.Empty<IssuedDocumentSummaryDto>());

    [Fact]
    public async Task ApplyAsync_HappyPath_DelegatesToUpdateMyContactAndReturnsPostMutationProfile()
    {
        // Arrange — substitute the profile service, set up the success path.
        var profiles = Substitute.For<IProfileService>();
        profiles.UpdateMyContactAsync(Arg.Any<ProfileContactInput>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));
        profiles.GetMineAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<ProfileOutput>.Success(PostUpdate)));

        var sut = new UiProfileManagementStrategy(profiles);
        var input = new ProfileManagementInput(
            DisplayName: "Citizen Renamed",
            Email: "renamed@example.md",
            Phone: "+37360123456");

        // Act
        var result = await sut.ApplyAsync(profileSqid: "ignored", input, CancellationToken.None);

        // Assert — strategy key is canonical AND result echoes GetMineAsync verbatim.
        sut.StrategyKey.Should().Be(ProfileManagementStrategyKeys.Ui);
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeSameAs(PostUpdate);

        // The contact-update call carries the input fields verbatim — the strategy
        // must NOT silently drop or rename keys before delegation.
        await profiles.Received(1).UpdateMyContactAsync(
            Arg.Is<ProfileContactInput>(c =>
                c.DisplayName == "Citizen Renamed"
                && c.Email == "renamed@example.md"
                && c.Phone == "+37360123456"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyAsync_MissingDisplayName_ReturnsValidationFailedWithoutCallingService()
    {
        // Arrange
        var profiles = Substitute.For<IProfileService>();
        var sut = new UiProfileManagementStrategy(profiles);
        var input = new ProfileManagementInput(DisplayName: "  "); // whitespace counts as missing

        // Act
        var result = await sut.ApplyAsync(profileSqid: "u1", input, CancellationToken.None);

        // Assert — boundary rejection BEFORE the service is touched (fail-fast).
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
        await profiles.DidNotReceive().UpdateMyContactAsync(
            Arg.Any<ProfileContactInput>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyAsync_UpdateFails_PropagatesErrorCodeAndDoesNotReadBack()
    {
        // Arrange — update returns a stable failure code (e.g. InvalidPhone).
        var profiles = Substitute.For<IProfileService>();
        profiles.UpdateMyContactAsync(Arg.Any<ProfileContactInput>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Failure(ErrorCodes.InvalidPhone, "bad phone")));

        var sut = new UiProfileManagementStrategy(profiles);
        var input = new ProfileManagementInput(DisplayName: "Has Name", Phone: "bogus");

        // Act
        var result = await sut.ApplyAsync(profileSqid: "u1", input, CancellationToken.None);

        // Assert — the failure code is forwarded verbatim AND we never round-trip
        // GetMineAsync (no point reading a row we just refused to write).
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidPhone);
        await profiles.DidNotReceive().GetMineAsync(Arg.Any<CancellationToken>());
    }
}
