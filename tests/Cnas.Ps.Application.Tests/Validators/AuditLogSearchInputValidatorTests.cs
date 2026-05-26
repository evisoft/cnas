using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;

namespace Cnas.Ps.Application.Tests.Validators;

/// <summary>
/// R0193 / TOR SEC 052 — FluentValidation rules for
/// <see cref="AuditLogSearchInputValidator"/>. Exercises the Take/Skip caps
/// and the date-range monotonicity invariant.
/// </summary>
public sealed class AuditLogSearchInputValidatorTests
{
    private static AuditLogSearchInput Valid() => new(
        Filter: null,
        FromUtc: null,
        ToUtc: null,
        Skip: 0,
        Take: 50);

    [Fact]
    public void Valid_Input_Passes()
    {
        var v = new AuditLogSearchInputValidator();
        var result = v.Validate(Valid());
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Take_Above_Cap_Rejected()
    {
        var v = new AuditLogSearchInputValidator();
        var result = v.Validate(Valid() with { Take = 300 });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(AuditLogSearchInput.Take));
    }

    [Fact]
    public void From_After_To_Rejected()
    {
        var v = new AuditLogSearchInputValidator();
        var input = Valid() with
        {
            FromUtc = new DateTime(2026, 5, 21, 12, 0, 0, DateTimeKind.Utc),
            ToUtc = new DateTime(2026, 5, 21, 10, 0, 0, DateTimeKind.Utc),
        };

        var result = v.Validate(input);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(AuditLogSearchInput.FromUtc));
    }

    [Fact]
    public void Take_Zero_Or_Negative_Rejected()
    {
        var v = new AuditLogSearchInputValidator();
        v.Validate(Valid() with { Take = 0 }).IsValid.Should().BeFalse();
        v.Validate(Valid() with { Take = -5 }).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Skip_Negative_Rejected()
    {
        var v = new AuditLogSearchInputValidator();
        var result = v.Validate(Valid() with { Skip = -1 });
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(AuditLogSearchInput.Skip));
    }

    [Fact]
    public void Only_From_Supplied_Is_Accepted()
    {
        var v = new AuditLogSearchInputValidator();
        var input = Valid() with { FromUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
        var result = v.Validate(input);
        result.IsValid.Should().BeTrue();
    }
}
