using System;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using FluentValidation.TestHelper;

namespace Cnas.Ps.Application.Tests.Validators;

/// <summary>
/// R0580 / TOR CF 09.02 — FluentValidation tests for
/// <see cref="AdHocReportSpecValidator"/>.
/// </summary>
public sealed class AdHocReportSpecValidatorTests
{
    /// <summary>The SUT under test (stateless — reused across all facts).</summary>
    private static readonly AdHocReportSpecValidator Sut = new();

    /// <summary>Reused happy-path column projection (CA1861 — no inline new[] arrays).</summary>
    private static readonly string[] HappyColumns = ["ReferenceNumber", "Status"];

    /// <summary>Reused minimal column projection.</summary>
    private static readonly string[] IdOnly = ["Id"];

    /// <summary>Reused empty filter list.</summary>
    private static readonly AdHocReportFilterDto[] NoFilters = Array.Empty<AdHocReportFilterDto>();

    /// <summary>One unknown-operator filter.</summary>
    private static readonly AdHocReportFilterDto[] UnknownOperatorFilter =
    [
        new AdHocReportFilterDto("Id", "BETWEEN", "1"),
    ];

    /// <summary>One filter with an empty Field.</summary>
    private static readonly AdHocReportFilterDto[] EmptyFieldFilter =
    [
        new AdHocReportFilterDto(string.Empty, AdHocReportOperators.Eq, "1"),
    ];

    /// <summary>Baseline OK payload validates clean.</summary>
    [Fact]
    public void Validate_HappyPath_NoErrors()
    {
        var spec = new AdHocReportSpecDto(
            AdHocReportEntitySets.Applications,
            HappyColumns,
            NoFilters,
            OrderBy: null,
            Descending: false);

        var result = Sut.TestValidate(spec);
        result.ShouldNotHaveAnyValidationErrors();
    }

    /// <summary>Unknown EntitySet is rejected.</summary>
    [Fact]
    public void Validate_UnknownEntitySet_Fails()
    {
        var spec = new AdHocReportSpecDto(
            "WidgetBaskets",
            IdOnly,
            NoFilters,
            OrderBy: null,
            Descending: false);

        var result = Sut.TestValidate(spec);
        result.ShouldHaveValidationErrorFor(s => s.EntitySet);
    }

    /// <summary>Empty Columns list is rejected.</summary>
    [Fact]
    public void Validate_EmptyColumns_Fails()
    {
        var spec = new AdHocReportSpecDto(
            AdHocReportEntitySets.Applications,
            Array.Empty<string>(),
            NoFilters,
            OrderBy: null,
            Descending: false);

        var result = Sut.TestValidate(spec);
        result.ShouldHaveValidationErrorFor(s => s.Columns);
    }

    /// <summary>Columns list with > 20 entries is rejected.</summary>
    [Fact]
    public void Validate_TooManyColumns_Fails()
    {
        var cols = new string[AdHocReportSpecValidator.MaxColumns + 1];
        for (var i = 0; i < cols.Length; i++) cols[i] = $"Col{i}";
        var spec = new AdHocReportSpecDto(
            AdHocReportEntitySets.Applications,
            cols,
            NoFilters,
            OrderBy: null,
            Descending: false);

        var result = Sut.TestValidate(spec);
        result.ShouldHaveValidationErrorFor(s => s.Columns);
    }

    /// <summary>Unknown filter operator is rejected.</summary>
    [Fact]
    public void Validate_UnknownFilterOperator_Fails()
    {
        var spec = new AdHocReportSpecDto(
            AdHocReportEntitySets.Applications,
            IdOnly,
            UnknownOperatorFilter,
            OrderBy: null,
            Descending: false);

        var result = Sut.TestValidate(spec);
        result.ShouldHaveValidationErrorFor("Filters[0].Operator");
    }

    /// <summary>Empty filter Field is rejected.</summary>
    [Fact]
    public void Validate_EmptyFilterField_Fails()
    {
        var spec = new AdHocReportSpecDto(
            AdHocReportEntitySets.Applications,
            IdOnly,
            EmptyFieldFilter,
            OrderBy: null,
            Descending: false);

        var result = Sut.TestValidate(spec);
        result.ShouldHaveValidationErrorFor("Filters[0].Field");
    }
}
