using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Services.IntlAgreements.Policies;

namespace Cnas.Ps.Infrastructure.Tests.IntlAgreements;

/// <summary>
/// R1201 / R1402 / TOR §3.4-B / §3.6-C — policy-level tests pinning the
/// reviewer-role codes + benefit-kind discriminators on each shipped
/// <c>IIntlAgreementRoutingPolicy</c>. Future role-catalogue rename will
/// fail this test deliberately so it surfaces in CI.
/// </summary>
public sealed class IntlAgreementRoutingPolicyTests
{
    [Fact]
    public void IncapacityMaternityPolicy_ReportsExpectedRoleCodes()
    {
        var policy = new IncapacityMaternityIntlAgreementRoutingPolicy();

        policy.BenefitKind.Should().Be(IntlAgreementBenefitKind.IncapacityMaternity);
        policy.LocalReviewerRoleCode.Should().Be("IMR_LOCAL_OFFICE_REVIEWER");
        policy.RegionalReviewerRoleCode.Should().Be("IMR_REGIONAL_OFFICE_REVIEWER");
        policy.NationalReviewerRoleCode.Should().Be("IMR_NATIONAL_INTL_REVIEWER");
        policy.DisplayLabel.Should().Contain("Sick leave");
    }

    [Fact]
    public void UnemploymentPolicy_ReportsExpectedRoleCodes()
    {
        var policy = new UnemploymentIntlAgreementRoutingPolicy();

        policy.BenefitKind.Should().Be(IntlAgreementBenefitKind.Unemployment);
        policy.LocalReviewerRoleCode.Should().Be("UI_LOCAL_OFFICE_REVIEWER");
        policy.RegionalReviewerRoleCode.Should().Be("UI_REGIONAL_OFFICE_REVIEWER");
        policy.NationalReviewerRoleCode.Should().Be("UI_NATIONAL_INTL_REVIEWER");
        policy.DisplayLabel.Should().Contain("Unemployment");
    }

    [Fact]
    public void IncapacityMaternityPolicy_BadJson_ReturnsFailure()
    {
        var policy = new IncapacityMaternityIntlAgreementRoutingPolicy();

        var result = policy.ValidateEvidence("{ not really json");

        result.IsFailure.Should().BeTrue();
        result.ErrorMessage.Should().Contain("BAD_JSON");
    }

    [Fact]
    public void UnemploymentPolicy_NonObjectJson_ReturnsFailure()
    {
        var policy = new UnemploymentIntlAgreementRoutingPolicy();

        var result = policy.ValidateEvidence("[1,2,3]");

        result.IsFailure.Should().BeTrue();
        result.ErrorMessage.Should().Contain("NOT_OBJECT");
    }
}
