using Cnas.Ps.Application.Prefill;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using FluentValidation.TestHelper;

namespace Cnas.Ps.Application.Tests.Prefill;

/// <summary>
/// R0552 / R0562 — validator tests for <see cref="PrefillRequestDto"/>. Confirms the
/// allow-list rejection (unknown source / unknown field) and the 50-field cap.
/// </summary>
public sealed class PrefillRequestDtoValidatorTests
{
    private readonly PrefillRequestDtoValidator _sut = new();

    /// <summary>11. Unknown source string is rejected with a field-level error.</summary>
    [Fact]
    public void R0552_Validate_UnknownSource_FailsValidation()
    {
        var sources = new List<string> { "FOO" };
        var dto = new PrefillRequestDto(sources, null);

        var result = _sut.TestValidate(dto);

        result.ShouldHaveValidationErrorFor("Sources[0]");
    }

    /// <summary>12. Fields list of 51 entries is rejected with a count error.</summary>
    [Fact]
    public void R0552_Validate_TooManyFields_FailsValidation()
    {
        var fields = Enumerable.Repeat(PrefillFields.FullName, 51).ToArray();
        var dto = new PrefillRequestDto(null, fields);

        var result = _sut.TestValidate(dto);

        result.ShouldHaveValidationErrorFor(x => x.Fields!.Count);
    }

    /// <summary>13. Unknown field name is rejected with a field-level error.</summary>
    [Fact]
    public void R0552_Validate_UnknownFieldName_FailsValidation()
    {
        var fields = new List<string> { "TotallyMadeUpField" };
        var dto = new PrefillRequestDto(null, fields);

        var result = _sut.TestValidate(dto);

        result.ShouldHaveValidationErrorFor("Fields[0]");
    }

    /// <summary>A request with valid sources and valid fields passes validation.</summary>
    [Fact]
    public void R0552_Validate_KnownSourcesAndFields_Passes()
    {
        var sources = new List<string> { PrefillSources.Rsp, PrefillSources.SiSfs };
        var fields = new List<string> { PrefillFields.FullName, PrefillFields.Email };
        var dto = new PrefillRequestDto(sources, fields);

        var result = _sut.TestValidate(dto);

        result.ShouldNotHaveAnyValidationErrors();
    }

    /// <summary>Null lists are valid (defaults apply downstream).</summary>
    [Fact]
    public void R0552_Validate_NullLists_Passes()
    {
        var dto = new PrefillRequestDto(null, null);

        var result = _sut.TestValidate(dto);

        result.ShouldNotHaveAnyValidationErrors();
    }
}
