using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;

namespace Cnas.Ps.Application.Tests.Validators;

/// <summary>
/// R0361 / UC13 — validation rules for <see cref="ProfileContactInputValidator"/>.
/// The validator gates the body accepted by <c>PUT /api/profile/contact</c>: a
/// thin self-service surface that lets a citizen update their display name,
/// e-mail, and phone number from the <c>MyProfile.razor</c> page without
/// touching language or notification preferences (those have dedicated PUTs).
/// </summary>
/// <remarks>
/// Per CLAUDE.md RULE 1 the assertions in this file were authored BEFORE
/// <see cref="ProfileContactInputValidator"/> existed; they pin the contract
/// that the matching production code is then implemented to satisfy.
/// </remarks>
public sealed class ProfileContactInputValidatorTests
{
    /// <summary>Happy path — a full, well-formed payload validates clean.</summary>
    [Fact]
    public void Validate_WellFormedPayload_Succeeds()
    {
        var sut = new ProfileContactInputValidator();

        var result = sut.Validate(new ProfileContactInput(
            DisplayName: "Ion Popescu",
            Email: "ion@example.md",
            Phone: "+37369123456"));

        result.IsValid.Should().BeTrue(string.Join("; ", result.Errors));
    }

    /// <summary>
    /// DisplayName is required — the field anchors the row's human-readable label
    /// in the staff console; an empty value would render as blank everywhere it
    /// appears. The validator must reject null / whitespace.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_DisplayNameMissing_Fails(string? displayName)
    {
        var sut = new ProfileContactInputValidator();

        var result = sut.Validate(new ProfileContactInput(
            DisplayName: displayName!,
            Email: "ion@example.md",
            Phone: "+37369123456"));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(ProfileContactInput.DisplayName));
    }

    /// <summary>
    /// Email is optional — citizens may clear their address with a null value
    /// — but when supplied it must parse as an RFC e-mail. Garbage like
    /// "not-an-email" must be rejected at the boundary so we never persist a
    /// value that would silently fail at notification-send time.
    /// </summary>
    [Theory]
    [InlineData("not-an-email")]
    [InlineData("missing-at-sign.md")]
    [InlineData("two@@example.md")]
    public void Validate_InvalidEmail_Fails(string email)
    {
        var sut = new ProfileContactInputValidator();

        var result = sut.Validate(new ProfileContactInput(
            DisplayName: "Ion Popescu",
            Email: email,
            Phone: null));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(ProfileContactInput.Email));
    }

    /// <summary>
    /// Phone is optional (null clears it) — but when supplied it must look like
    /// an E.164 number. The deeper canonicalisation is performed by
    /// <c>PhoneE164.TryCreate</c> in the service; the validator's role is the
    /// shape check so callers receive a fast 400 without round-tripping to
    /// the database.
    /// </summary>
    [Theory]
    [InlineData("123-456-7890")] // missing leading '+'
    [InlineData("notaphone")]
    [InlineData("+")]
    public void Validate_InvalidPhone_Fails(string phone)
    {
        var sut = new ProfileContactInputValidator();

        var result = sut.Validate(new ProfileContactInput(
            DisplayName: "Ion Popescu",
            Email: null,
            Phone: phone));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(ProfileContactInput.Phone));
    }

    /// <summary>
    /// Null Email + Null Phone is a valid "I am only renaming myself" payload.
    /// The validator must not require contact fields to be supplied — the only
    /// hard requirement is DisplayName.
    /// </summary>
    [Fact]
    public void Validate_OnlyDisplayName_Succeeds()
    {
        var sut = new ProfileContactInputValidator();

        var result = sut.Validate(new ProfileContactInput(
            DisplayName: "Ion Popescu",
            Email: null,
            Phone: null));

        result.IsValid.Should().BeTrue(string.Join("; ", result.Errors));
    }
}
