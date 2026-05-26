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
/// <see cref="FormProfileManagementStrategy"/>. The Form channel routes a
/// paper / front-desk submission through the iter-128 form-intake schema
/// validator and then forwards the extracted contact fields to the
/// UI-equivalent path. Tests pin both legs.
/// </summary>
public sealed class FormProfileManagementStrategyTests
{
    /// <summary>Canonical post-mutation projection returned by the stubbed GET.</summary>
    private static readonly ProfileOutput PostUpdate = new(
        "userSqid",
        "Form Submitted",
        "form@example.md",
        null,
        "ro",
        Array.Empty<IssuedDocumentSummaryDto>());

    [Fact]
    public async Task ApplyAsync_HappyPath_ValidatesFormAndAppliesUpdate()
    {
        // Arrange — intake validation passes, the profile service accepts.
        var intake = Substitute.For<IFormIntakeService>();
        intake.ValidateAsync("PP-1", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));
        var profiles = Substitute.For<IProfileService>();
        profiles.UpdateMyContactAsync(Arg.Any<ProfileContactInput>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));
        profiles.GetMineAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<ProfileOutput>.Success(PostUpdate)));

        var sut = new FormProfileManagementStrategy(intake, profiles);
        var input = new ProfileManagementInput(
            ServicePassportSqid: "PP-1",
            FormPayloadJson: "{\"displayName\":\"Form Submitted\",\"email\":\"form@example.md\"}");

        // Act
        var result = await sut.ApplyAsync(profileSqid: "u1", input, CancellationToken.None);

        // Assert — strategy key + happy-path round-trip.
        sut.StrategyKey.Should().Be(ProfileManagementStrategyKeys.Form);
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeSameAs(PostUpdate);

        // Profile-update call was driven by the form payload keys.
        await profiles.Received(1).UpdateMyContactAsync(
            Arg.Is<ProfileContactInput>(c =>
                c.DisplayName == "Form Submitted"
                && c.Email == "form@example.md"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyAsync_SchemaValidationFails_ReturnsValidationFailedAndDoesNotApply()
    {
        // Arrange — intake validation rejects the payload.
        var intake = Substitute.For<IFormIntakeService>();
        intake.ValidateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Failure(ErrorCodes.ValidationFailed, "bad form")));
        var profiles = Substitute.For<IProfileService>();
        var sut = new FormProfileManagementStrategy(intake, profiles);
        var input = new ProfileManagementInput(
            ServicePassportSqid: "PP-1",
            FormPayloadJson: "{}");

        // Act
        var result = await sut.ApplyAsync(profileSqid: "u1", input, CancellationToken.None);

        // Assert — validation code propagates AND the profile service is never touched.
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
        await profiles.DidNotReceive().UpdateMyContactAsync(
            Arg.Any<ProfileContactInput>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyAsync_MissingPayload_ReturnsValidationFailedBeforeIntake()
    {
        // Arrange — neither ServicePassportSqid nor FormPayloadJson supplied.
        var intake = Substitute.For<IFormIntakeService>();
        var profiles = Substitute.For<IProfileService>();
        var sut = new FormProfileManagementStrategy(intake, profiles);
        var input = new ProfileManagementInput(DisplayName: "ignored");

        // Act
        var result = await sut.ApplyAsync(profileSqid: "u1", input, CancellationToken.None);

        // Assert — fail-fast at the boundary; the intake validator is not invoked.
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
        await intake.DidNotReceive().ValidateAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyAsync_PayloadValidButMissingDisplayName_ReturnsFormIntakePayloadInvalid()
    {
        // Arrange — intake validation passes (schema OK) but the payload lacks the
        // displayName key the strategy requires to forward to the profile-update.
        var intake = Substitute.For<IFormIntakeService>();
        intake.ValidateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));
        var profiles = Substitute.For<IProfileService>();
        var sut = new FormProfileManagementStrategy(intake, profiles);
        var input = new ProfileManagementInput(
            ServicePassportSqid: "PP-1",
            FormPayloadJson: "{\"email\":\"only-email@example.md\"}");

        // Act
        var result = await sut.ApplyAsync(profileSqid: "u1", input, CancellationToken.None);

        // Assert — dedicated code distinguishes "schema OK but no profile keys"
        // from a structural schema failure.
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ProfileFormIntakePayloadInvalid);
    }
}
