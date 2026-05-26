using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;

namespace Cnas.Ps.Application.Tests.Validators;

/// <summary>
/// R0160 / R0161 / TOR CF 03.03 — FluentValidation rules for
/// <see cref="GlobalSearchInputValidator"/>. Exercises the query / domain /
/// paging guards.
/// </summary>
public sealed class GlobalSearchInputValidatorTests
{
    /// <summary>Returns a known-valid input the per-test cases mutate.</summary>
    private static GlobalSearchInputDto Valid() => new(
        Query: "alpha",
        Domains: null,
        Skip: 0,
        Take: 20);

    /// <summary>The canonical happy-path input must validate.</summary>
    [Fact]
    public void Valid_Input_Passes()
    {
        var v = new GlobalSearchInputValidator();
        v.Validate(Valid()).IsValid.Should().BeTrue();
    }

    /// <summary>Empty query is rejected with a Query-scoped error.</summary>
    [Fact]
    public void Empty_Query_Rejected()
    {
        var v = new GlobalSearchInputValidator();
        var result = v.Validate(Valid() with { Query = string.Empty });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(GlobalSearchInputDto.Query));
    }

    /// <summary>Whitespace-only query is rejected (NotEmpty + trim-equality both fire).</summary>
    [Fact]
    public void Whitespace_Query_Rejected()
    {
        var v = new GlobalSearchInputValidator();
        var result = v.Validate(Valid() with { Query = "   " });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(GlobalSearchInputDto.Query));
    }

    /// <summary>Leading whitespace surfaces an explicit error rather than silent trim.</summary>
    [Fact]
    public void Leading_Trailing_Whitespace_Rejected()
    {
        var v = new GlobalSearchInputValidator();
        v.Validate(Valid() with { Query = "  alpha" }).IsValid.Should().BeFalse();
        v.Validate(Valid() with { Query = "alpha  " }).IsValid.Should().BeFalse();
    }

    /// <summary>Query strictly longer than the cap is rejected.</summary>
    [Fact]
    public void Query_Above_Cap_Rejected()
    {
        var v = new GlobalSearchInputValidator();
        var tooLong = new string('a', GlobalSearchInputValidator.MaxQueryLength + 1);
        v.Validate(Valid() with { Query = tooLong }).IsValid.Should().BeFalse();
    }

    /// <summary>Take above the hard cap is rejected.</summary>
    [Fact]
    public void Take_Above_Cap_Rejected()
    {
        var v = new GlobalSearchInputValidator();
        var result = v.Validate(Valid() with { Take = GlobalSearchInputValidator.MaxTake + 1 });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(GlobalSearchInputDto.Take));
    }

    /// <summary>Take = 0 / negative is rejected.</summary>
    [Fact]
    public void Take_Zero_Or_Negative_Rejected()
    {
        var v = new GlobalSearchInputValidator();
        v.Validate(Valid() with { Take = 0 }).IsValid.Should().BeFalse();
        v.Validate(Valid() with { Take = -5 }).IsValid.Should().BeFalse();
    }

    /// <summary>Negative skip is rejected.</summary>
    [Fact]
    public void Skip_Negative_Rejected()
    {
        var v = new GlobalSearchInputValidator();
        var result = v.Validate(Valid() with { Skip = -1 });
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(GlobalSearchInputDto.Skip));
    }

    /// <summary>Static cache used by the unknown-domain test (CA1861 — avoid per-call allocation).</summary>
    private static readonly string[] UnknownDomainList = ["applications", "moon-base"];

    /// <summary>Static cache used by the case-insensitive test (CA1861 — avoid per-call allocation).</summary>
    private static readonly string[] MixedCaseDomainList = ["Applications", "INSURED-PERSONS"];

    /// <summary>Unknown domain code is rejected.</summary>
    [Fact]
    public void Unknown_Domain_Rejected()
    {
        var v = new GlobalSearchInputValidator();
        var result = v.Validate(Valid() with { Domains = UnknownDomainList });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(GlobalSearchInputDto.Domains));
    }

    /// <summary>Known domain codes (case-insensitive) are accepted.</summary>
    [Fact]
    public void Known_Domains_Accepted_CaseInsensitive()
    {
        var v = new GlobalSearchInputValidator();
        v.Validate(Valid() with { Domains = MixedCaseDomainList })
            .IsValid.Should().BeTrue();
    }

    /// <summary>The catalogue helper recognises every canonical code.</summary>
    [Fact]
    public void IsKnownDomain_Covers_All_Canonical_Codes()
    {
        foreach (var code in GlobalSearchDomains.All)
        {
            GlobalSearchInputValidator.IsKnownDomain(code).Should().BeTrue($"'{code}' is canonical");
        }

        GlobalSearchInputValidator.IsKnownDomain(null).Should().BeFalse();
        GlobalSearchInputValidator.IsKnownDomain(string.Empty).Should().BeFalse();
    }
}
