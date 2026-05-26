using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;

namespace Cnas.Ps.Application.Tests.Validators;

/// <summary>
/// R2279 / TOR SEC 033 — unit tests for the classification-catalog input
/// validators. Covers happy-path filter envelopes, the lower / upper page
/// bounds, and the acknowledgement-note length rule.
/// </summary>
public sealed class ClassificationCatalogInputValidatorTests
{
    [Fact]
    public void EntryFilterValidator_HappyPath_Accepts()
    {
        var validator = new ClassificationCatalogEntryFilterValidator();
        var filter = new ClassificationCatalogEntryFilterDto(
            Label: "Internal",
            IsExplicit: true,
            TypeFullNameContains: "Cnas.Ps.Contracts",
            Skip: 0,
            Take: 100);

        var result = validator.Validate(filter);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void EntryFilterValidator_RejectsTakeZero()
    {
        var validator = new ClassificationCatalogEntryFilterValidator();
        var filter = new ClassificationCatalogEntryFilterDto(
            Label: null,
            IsExplicit: null,
            TypeFullNameContains: null,
            Skip: 0,
            Take: 0);

        var result = validator.Validate(filter);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(filter.Take));
    }

    [Fact]
    public void EntryFilterValidator_RejectsUnknownLabel()
    {
        var validator = new ClassificationCatalogEntryFilterValidator();
        var filter = new ClassificationCatalogEntryFilterDto(
            Label: "Secret", // Not a SensitivityLabel name.
            IsExplicit: null,
            TypeFullNameContains: null,
            Skip: 0,
            Take: 10);

        var result = validator.Validate(filter);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(filter.Label));
    }

    [Fact]
    public void DriftFilterValidator_RejectsTakeAboveMax()
    {
        var validator = new ClassificationDriftFilterValidator();
        var filter = new ClassificationDriftFilterDto(
            DriftKind: null,
            Acknowledged: null,
            Skip: 0,
            Take: 999);

        var result = validator.Validate(filter);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(filter.Take));
    }

    [Fact]
    public void AckValidator_RejectsTooShortNote()
    {
        var validator = new ClassificationDriftAcknowledgeInputValidator();
        var input = new ClassificationDriftAcknowledgeInputDto("ab");

        var result = validator.Validate(input);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(input.Note));
    }

    [Fact]
    public void AckValidator_AcceptsValidNote()
    {
        var validator = new ClassificationDriftAcknowledgeInputValidator();
        var input = new ClassificationDriftAcknowledgeInputDto("Reviewed — label change matches ARH 028 update.");

        var result = validator.Validate(input);

        result.IsValid.Should().BeTrue();
    }
}
