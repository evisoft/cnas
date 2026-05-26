using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;

namespace Cnas.Ps.Application.Tests.Validators;

/// <summary>
/// R0830 / R0834 / TOR Annex 1 §8.1.4.5 — pins the validator contract on
/// <see cref="InsolvencyOpenInputValidator"/>. Tests use the clock-anchored
/// overload so the future-date guard stays deterministic.
/// </summary>
public sealed class InsolvencyOpenInputValidatorTests
{
    /// <summary>Stable anchor — the test bounds are independent of wall-clock time.</summary>
    private static readonly DateOnly Today = new(2026, 5, 25);

    /// <summary>Returns a well-formed input.</summary>
    private static InsolvencyOpenInputDto Good() => new(
        ContributorSqid: "SQID-1",
        Reason: "Hotărâre judecătorească nr. 1234/2026",
        InsolvencyDate: Today);

    [Fact]
    public void HappyPath_Accepted()
    {
        var v = new InsolvencyOpenInputValidator(Today);
        v.Validate(Good()).IsValid.Should().BeTrue();
    }

    [Fact]
    public void EmptyContributorSqid_Rejected()
    {
        var v = new InsolvencyOpenInputValidator(Today);
        v.Validate(Good() with { ContributorSqid = string.Empty })
            .IsValid.Should().BeFalse();
    }

    [Fact]
    public void TooShortReason_Rejected()
    {
        var v = new InsolvencyOpenInputValidator(Today);
        v.Validate(Good() with { Reason = "ab" })
            .IsValid.Should().BeFalse();
    }

    [Fact]
    public void FutureInsolvencyDate_Rejected()
    {
        var v = new InsolvencyOpenInputValidator(Today);
        v.Validate(Good() with { InsolvencyDate = Today.AddDays(1) })
            .IsValid.Should().BeFalse();
    }

    [Fact]
    public void ResolveValidator_RejectsTooShortResolution()
    {
        var v = new InsolvencyResolveInputValidator();
        v.Validate(new InsolvencyResolveInputDto("ab"))
            .IsValid.Should().BeFalse();
    }
}
