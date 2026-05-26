using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using FluentValidation.TestHelper;

namespace Cnas.Ps.Application.Tests.Qbe;

/// <summary>
/// R0163 — FluentValidation tests for <see cref="QbeFilterDtoValidator"/>.
/// </summary>
public sealed class QbeFilterDtoValidatorTests
{
    private readonly QbeFilterDtoValidator _sut = new();

    /// <summary>Generates <paramref name="count"/> shaped <c>Equals</c> conditions on the canonical <c>Code</c> field.</summary>
    private static List<QbeConditionDto> ManyConditions(int count)
    {
        var list = new List<QbeConditionDto>(count);
        for (int i = 0; i < count; i++)
        {
            list.Add(new QbeConditionDto("Email", "Equals", $"a{i}@example.com"));
        }
        return list;
    }

    [Fact]
    public void Validator_RejectsTooManyConditions()
    {
        // Cap is 25; submit 26 → must fail with the conditions-list rule.
        var dto = new QbeFilterDto("AND", ManyConditions(26));

        var result = _sut.TestValidate(dto);
        result.ShouldHaveValidationErrorFor(x => x.Conditions);
    }

    [Fact]
    public void Validator_RejectsInvalidFieldNameCharacters()
    {
        // Field name carries non-identifier chars → must fail.
        var dto = new QbeFilterDto("AND", new[]
        {
            new QbeConditionDto("Drop;Table--", "Equals", "x"),
        });

        var result = _sut.TestValidate(dto);
        result.ShouldHaveValidationErrorFor("Conditions[0].FieldName");
    }

    [Fact]
    public void Validator_RejectsUnknownCombinator()
    {
        var dto = new QbeFilterDto("XOR", new[]
        {
            new QbeConditionDto("Email", "Equals", "a@b"),
        });

        var result = _sut.TestValidate(dto);
        result.ShouldHaveValidationErrorFor(x => x.Combinator);
    }

    [Fact]
    public void Validator_AcceptsCanonicalAndCombinator()
    {
        var dto = new QbeFilterDto("AND", new[]
        {
            new QbeConditionDto("Email", "Equals", "a@b"),
        });

        var result = _sut.TestValidate(dto);
        result.ShouldNotHaveAnyValidationErrors();
    }
}
