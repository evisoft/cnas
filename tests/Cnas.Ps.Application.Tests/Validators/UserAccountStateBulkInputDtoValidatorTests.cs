using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;

namespace Cnas.Ps.Application.Tests.Validators;

/// <summary>
/// R2263 / SEC 016 — tests for <see cref="UserAccountStateBulkInputDtoValidator"/>.
/// Verifies the (1..200) user-sqid envelope and the (3..500) reason envelope.
/// </summary>
public sealed class UserAccountStateBulkInputDtoValidatorTests
{
    [Fact]
    public void Validate_HappyPath_Passes()
    {
        var sut = new UserAccountStateBulkInputDtoValidator();
        var dto = new UserAccountStateBulkInputDto(
            UserSqids: ["abc123", "def456"],
            Reason: "Routine compliance suspension");

        var result = sut.Validate(dto);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_TooManyUserSqids_Fails()
    {
        var sut = new UserAccountStateBulkInputDtoValidator();
        var sqids = Enumerable.Range(0, 250).Select(i => $"sq{i}").ToArray();
        var dto = new UserAccountStateBulkInputDto(sqids, "valid reason");

        var result = sut.Validate(dto);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("200", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_TooShortReason_Fails()
    {
        var sut = new UserAccountStateBulkInputDtoValidator();
        var dto = new UserAccountStateBulkInputDto(["abc123"], Reason: "x");

        var result = sut.Validate(dto);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(UserAccountStateBulkInputDto.Reason));
    }

    [Fact]
    public void Validate_EmptyUserSqids_Fails()
    {
        var sut = new UserAccountStateBulkInputDtoValidator();
        var dto = new UserAccountStateBulkInputDto(Array.Empty<string>(), Reason: "valid reason");

        var result = sut.Validate(dto);

        result.IsValid.Should().BeFalse();
    }
}
