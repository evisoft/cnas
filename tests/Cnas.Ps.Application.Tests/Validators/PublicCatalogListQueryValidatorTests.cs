using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using FluentValidation.TestHelper;

namespace Cnas.Ps.Application.Tests.Validators;

/// <summary>
/// R0502 / R0504 / R0505 — unit tests for
/// <see cref="PublicCatalogListQueryValidator"/>. Asserts each rule on the
/// inbound <see cref="PublicCatalogListQueryDto"/> independently, mirroring the
/// established validator-test cadence (TDD-first per CLAUDE.md RULE 1).
/// </summary>
public sealed class PublicCatalogListQueryValidatorTests
{
    /// <summary>Single validator instance reused across tests (validators are stateless).</summary>
    private readonly PublicCatalogListQueryValidator _validator = new();

    /// <summary>Builds a known-good DTO so each test mutates exactly one field.</summary>
    private static PublicCatalogListQueryDto BuildValid(
        string? q = null,
        string? category = null,
        string sort = "Relevance",
        int skip = 0,
        int take = 50,
        string? language = "ro")
        => new(q, category, sort, skip, take, language);

    [Fact]
    public void Defaults_AreValid()
    {
        var result = _validator.TestValidate(BuildValid());
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData("Relevance")]
    [InlineData("relevance")]
    [InlineData("Alphabetical")]
    [InlineData("Created")]
    [InlineData("Updated")]
    [InlineData("UPDATED")]
    public void Sort_KnownValues_PassValidation(string sort)
    {
        var result = _validator.TestValidate(BuildValid(sort: sort));
        result.ShouldNotHaveValidationErrorFor(x => x.Sort);
    }

    [Theory]
    [InlineData("Foo")]
    [InlineData("Unknown")]
    [InlineData("Newest")]
    [InlineData("")]
    public void Sort_UnknownValue_FailsValidation(string sort)
    {
        var result = _validator.TestValidate(BuildValid(sort: sort));
        result.ShouldHaveValidationErrorFor(x => x.Sort);
    }

    [Fact]
    public void Take_AboveCap_FailsValidation()
    {
        var result = _validator.TestValidate(BuildValid(take: 300));
        result.ShouldHaveValidationErrorFor(x => x.Take);
    }

    [Fact]
    public void Take_AtCap_PassesValidation()
    {
        var result = _validator.TestValidate(BuildValid(take: PublicCatalogListQueryValidator.MaxTake));
        result.ShouldNotHaveValidationErrorFor(x => x.Take);
    }

    [Fact]
    public void Take_Zero_FailsValidation()
    {
        var result = _validator.TestValidate(BuildValid(take: 0));
        result.ShouldHaveValidationErrorFor(x => x.Take);
    }

    [Fact]
    public void Skip_Negative_FailsValidation()
    {
        var result = _validator.TestValidate(BuildValid(skip: -1));
        result.ShouldHaveValidationErrorFor(x => x.Skip);
    }

    [Fact]
    public void Q_TooLong_FailsValidation()
    {
        var oversized = new string('a', PublicCatalogListQueryValidator.MaxQueryLength + 1);
        var result = _validator.TestValidate(BuildValid(q: oversized));
        result.ShouldHaveValidationErrorFor(x => x.Q);
    }

    [Fact]
    public void Q_AtLimit_PassesValidation()
    {
        var atLimit = new string('a', PublicCatalogListQueryValidator.MaxQueryLength);
        var result = _validator.TestValidate(BuildValid(q: atLimit));
        result.ShouldNotHaveValidationErrorFor(x => x.Q);
    }
}
