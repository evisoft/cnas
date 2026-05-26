using System.Linq;
using System.Reflection;
using Cnas.Ps.Contracts;
using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Application.Tests.Sensitivity;

/// <summary>
/// R0228 / TOR SEC 033 — compile-time-anchored unit tests asserting that the DTOs
/// touched by this batch carry the expected sensitivity classification. These tests are
/// the "annotation lockfile": if a future refactor strips an annotation off a high-risk
/// field, the suite goes red before the change reaches review.
/// </summary>
public sealed class DtoSensitivityAnnotationTests
{
    [Fact]
    public void InsuredPersonOutput_Idnp_IsRestricted()
    {
        var label = LabelOf<InsuredPersonOutput>(nameof(InsuredPersonOutput.Idnp));

        label.Should().Be(SensitivityLabel.Restricted,
            "IDNP is the highest-sensitivity citizen attribute per SEC 033.");
    }

    [Fact]
    public void InsuredPersonOutput_Names_AreConfidential()
    {
        LabelOf<InsuredPersonOutput>(nameof(InsuredPersonOutput.LastName))
            .Should().Be(SensitivityLabel.Confidential);
        LabelOf<InsuredPersonOutput>(nameof(InsuredPersonOutput.FirstName))
            .Should().Be(SensitivityLabel.Confidential);
    }

    [Fact]
    public void InsuredPersonOutput_Id_IsPublic()
    {
        var label = LabelOf<InsuredPersonOutput>(nameof(InsuredPersonOutput.Id));

        label.Should().Be(SensitivityLabel.Public,
            "the Sqid id is opaque by design — no business intelligence leakage.");
    }

    [Fact]
    public void PensionSimulationDto_EstimatedMonthlyPension_IsConfidential()
    {
        var label = LabelOf<PensionSimulationDto>(nameof(PensionSimulationDto.EstimatedMonthlyPension));

        label.Should().Be(SensitivityLabel.Confidential,
            "the projected pension amount is a personal-finance figure.");
    }

    [Fact]
    public void PensionSimulationDto_FormulaDescriptionRo_IsInternal()
    {
        var label = LabelOf<PensionSimulationDto>(nameof(PensionSimulationDto.FormulaDescriptionRo));

        label.Should().Be(SensitivityLabel.Internal,
            "the formula text holds no PII — only the substituted projection variables.");
    }

    [Fact]
    public void PersonalAccountExtractDto_AccountCodeSqid_IsRestricted()
    {
        var label = LabelOf<PersonalAccountExtractDto>(nameof(PersonalAccountExtractDto.AccountCodeSqid));

        label.Should().Be(SensitivityLabel.Restricted,
            "the personal-account code is a high-sensitivity citizen identifier.");
    }

    [Fact]
    public void PersonalAccountEntryDto_ContributionPaidAmount_IsConfidential()
    {
        var label = LabelOf<PersonalAccountEntryDto>(nameof(PersonalAccountEntryDto.ContributionPaidAmount));

        label.Should().Be(SensitivityLabel.Confidential);
    }

    [Fact]
    public void ProfileOutput_Email_Phone_AreConfidential()
    {
        LabelOf<ProfileOutput>(nameof(ProfileOutput.Email))
            .Should().Be(SensitivityLabel.Confidential);
        LabelOf<ProfileOutput>(nameof(ProfileOutput.Phone))
            .Should().Be(SensitivityLabel.Confidential);
    }

    [Fact]
    public void SolicitantListItem_DisplayName_IsConfidential()
    {
        var label = LabelOf<SolicitantListItem>(nameof(SolicitantListItem.DisplayName));

        label.Should().Be(SensitivityLabel.Confidential);
    }

    [Fact]
    public void ContributorPeriodProjectionDto_PhoneE164_IsConfidential()
    {
        var label = LabelOf<ContributorPeriodProjectionDto>(
            nameof(ContributorPeriodProjectionDto.PhoneE164));

        label.Should().Be(SensitivityLabel.Confidential,
            "PhoneE164 is citizen contact PII per R0228 / SEC 033.");
    }

    [Fact]
    public void ContributorPeriodProjectionDto_Email_IsConfidential()
    {
        var label = LabelOf<ContributorPeriodProjectionDto>(
            nameof(ContributorPeriodProjectionDto.Email));

        label.Should().Be(SensitivityLabel.Confidential,
            "Email is citizen contact PII per R0228 / SEC 033.");
    }

    /// <summary>
    /// Convenience: returns the property's <see cref="SensitivityLabel"/> as seen by the
    /// reflection-based resolver (property attribute first, type attribute as floor,
    /// <see cref="SensitivityLabel.Internal"/> otherwise).
    /// </summary>
    /// <typeparam name="TDto">DTO under inspection.</typeparam>
    /// <param name="propertyName">Property name.</param>
    /// <returns>The effective sensitivity label.</returns>
    private static SensitivityLabel LabelOf<TDto>(string propertyName)
    {
        // Mirror SensitivityResolver semantics: property attribute wins; falls back to
        // type attribute; falls back to Internal default. Type attribute, when present,
        // acts as a floor — but is NOT a floor when absent (so an explicit Public
        // property does not get clobbered by the implicit Internal default).
        var typeAttr = typeof(TDto).GetCustomAttribute<SensitivityClassificationAttribute>(inherit: true);
        var propAttr = typeof(TDto).GetProperty(propertyName)?
            .GetCustomAttribute<SensitivityClassificationAttribute>(inherit: true);

        var propLabel = propAttr?.Label ?? typeAttr?.Label ?? SensitivityLabel.Internal;
        if (typeAttr is not null && typeAttr.Label > propLabel)
        {
            propLabel = typeAttr.Label;
        }

        return propLabel;
    }
}
