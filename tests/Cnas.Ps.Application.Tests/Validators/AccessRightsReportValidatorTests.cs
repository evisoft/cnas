using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;

namespace Cnas.Ps.Application.Tests.Validators;

/// <summary>
/// R2274 / TOR SEC 028 — unit tests for the access-rights report paging validator.
/// Verifies Skip ≥ 0 and Take 1..500 bounds.
/// </summary>
public sealed class AccessRightsReportValidatorTests
{
    /// <summary>R2274 — valid paging (Skip=0, Take=100) passes.</summary>
    [Fact]
    public void AccessRightsReportPagingValidator_AcceptsValidPaging()
    {
        var validator = new AccessRightsReportPagingValidator();
        var input = new AccessRightsReportPagingDto(Skip: 0, Take: 100, IncludeDisabledAccounts: false);

        var result = validator.Validate(input);

        result.IsValid.Should().BeTrue();
    }

    /// <summary>R2274 — negative skip is rejected.</summary>
    [Fact]
    public void AccessRightsReportPagingValidator_RejectsNegativeSkip()
    {
        var validator = new AccessRightsReportPagingValidator();
        var input = new AccessRightsReportPagingDto(Skip: -1, Take: 100, IncludeDisabledAccounts: false);

        var result = validator.Validate(input);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(input.Skip));
    }

    /// <summary>R2274 — take above 500 is rejected.</summary>
    [Fact]
    public void AccessRightsReportPagingValidator_RejectsTakeOver500()
    {
        var validator = new AccessRightsReportPagingValidator();
        var input = new AccessRightsReportPagingDto(Skip: 0, Take: 501, IncludeDisabledAccounts: false);

        var result = validator.Validate(input);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(input.Take));
    }
}
