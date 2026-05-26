using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;

namespace Cnas.Ps.Application.Tests.Validators;

/// <summary>
/// R0671 continuation — unit tests for <see cref="DocumentsListInputValidator"/>.
/// Mirrors the rule-set asserted in <c>AuditLogSearchInputValidatorTests</c>.
/// </summary>
public sealed class DocumentsListInputValidatorTests
{
    /// <summary>
    /// Above-cap Take must be rejected so a malformed wire payload doesn't reach
    /// the service layer.
    /// </summary>
    [Fact]
    public void Validator_RejectsTakeAboveCap()
    {
        var validator = new DocumentsListInputValidator();
        var input = new DocumentsListInput(Take: 500);

        var result = validator.Validate(input);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(DocumentsListInput.Take));
    }

    /// <summary>Default 50-row Take is accepted as the happy path.</summary>
    [Fact]
    public void Validator_AcceptsDefaultEnvelope()
    {
        var validator = new DocumentsListInputValidator();
        var input = new DocumentsListInput();

        var result = validator.Validate(input);

        result.IsValid.Should().BeTrue();
    }

    /// <summary>Negative Skip rejected.</summary>
    [Fact]
    public void Validator_RejectsNegativeSkip()
    {
        var validator = new DocumentsListInputValidator();
        var input = new DocumentsListInput(Skip: -1);

        var result = validator.Validate(input);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(DocumentsListInput.Skip));
    }

    /// <summary>FromUtc &gt; ToUtc rejected; single-bounded ranges are legal.</summary>
    [Fact]
    public void Validator_RejectsInvertedDateRange()
    {
        var validator = new DocumentsListInputValidator();
        var input = new DocumentsListInput(
            FromUtc: new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            ToUtc: new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc));

        var result = validator.Validate(input);

        result.IsValid.Should().BeFalse();
    }
}
