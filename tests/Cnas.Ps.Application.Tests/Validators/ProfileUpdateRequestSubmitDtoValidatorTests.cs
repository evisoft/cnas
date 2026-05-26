using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;

namespace Cnas.Ps.Application.Tests.Validators;

/// <summary>
/// R0362 — validator tests for <see cref="ProfileUpdateRequestSubmitDtoValidator"/>.
/// Locks the three submit-time rules: <c>Type</c> must parse, <c>RequestedChangesJson</c>
/// must be syntactically valid JSON, and <c>TargetContributorSqid</c> must be present.
/// </summary>
public sealed class ProfileUpdateRequestSubmitDtoValidatorTests
{
    /// <summary>Submission with a happy-path payload validates clean.</summary>
    [Fact]
    public void Submission_WithKnownTypeAndValidJson_IsValid()
    {
        var v = new ProfileUpdateRequestSubmitDtoValidator();
        var dto = new ProfileUpdateRequestSubmitDto(
            TargetContributorSqid: "abc123",
            Type: "Address",
            RequestedChangesJson: "{\"street\":\"S\"}",
            Note: null);

        var result = v.Validate(dto);

        result.IsValid.Should().BeTrue();
    }

    /// <summary>Type strings outside the enum range are rejected.</summary>
    [Fact]
    public void Submission_WithUnknownType_IsRejected()
    {
        var v = new ProfileUpdateRequestSubmitDtoValidator();
        var dto = new ProfileUpdateRequestSubmitDto(
            TargetContributorSqid: "abc123",
            Type: "Unsupported",
            RequestedChangesJson: "{}",
            Note: null);

        var result = v.Validate(dto);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "Type");
    }

    /// <summary>Malformed JSON is rejected before the service ever sees the payload.</summary>
    [Fact]
    public void Submission_WithInvalidJson_IsRejected()
    {
        var v = new ProfileUpdateRequestSubmitDtoValidator();
        var dto = new ProfileUpdateRequestSubmitDto(
            TargetContributorSqid: "abc123",
            Type: "Address",
            RequestedChangesJson: "{not valid json",
            Note: null);

        var result = v.Validate(dto);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "RequestedChangesJson");
    }

    /// <summary>Missing <c>TargetContributorSqid</c> is rejected.</summary>
    [Fact]
    public void Submission_WithMissingTarget_IsRejected()
    {
        var v = new ProfileUpdateRequestSubmitDtoValidator();
        var dto = new ProfileUpdateRequestSubmitDto(
            TargetContributorSqid: "",
            Type: "Address",
            RequestedChangesJson: "{}",
            Note: null);

        var result = v.Validate(dto);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "TargetContributorSqid");
    }
}
