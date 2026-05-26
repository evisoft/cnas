using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using FluentValidation.TestHelper;

namespace Cnas.Ps.Application.Tests.Validators;

/// <summary>
/// Unit tests for <see cref="InsuredPersonRegistrationInputValidator"/> covering every
/// per-field rule plus a positive happy-path baseline (CLAUDE.md §3.3 — unit tier).
/// </summary>
public sealed class InsuredPersonRegistrationInputValidatorTests
{
    /// <summary>
    /// Deterministic "today" used for the BirthDate-in-the-past rule. The validator
    /// reads its clock from a static <c>Clock</c> property that this test class swaps
    /// to a fake before every Act call — see <see cref="WithClock"/>.
    /// </summary>
    private static readonly DateOnly Today = new(2026, 5, 19);

    /// <summary>
    /// Deterministic fake clock — pinned to <see cref="Today"/> so tests are reproducible
    /// across CI runners. Identical pattern to the Annex 3 scenario tests.
    /// </summary>
    private sealed class FakeClock : ICnasTimeProvider
    {
        public DateTime UtcNow { get; }

        public FakeClock(DateOnly today)
        {
            UtcNow = today.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        }
    }

    /// <summary>Single validator instance — validators are stateless modulo the static clock.</summary>
    private readonly InsuredPersonRegistrationInputValidator _validator;

    /// <summary>Wires the static clock to <see cref="Today"/> before instantiating the SUT.</summary>
    public InsuredPersonRegistrationInputValidatorTests()
    {
        InsuredPersonRegistrationInputValidator.Clock = new FakeClock(Today);
        _validator = new InsuredPersonRegistrationInputValidator();
    }

    /// <summary>
    /// Canonical valid IDNP. The mod-10 weighted checksum for prefix "200012345678" is 2
    /// (weights {7,3,1} cycling, sum 128, check = (10 - 128%10) % 10 = 2), giving the
    /// canonical 13-digit value below. The same construction is exercised in
    /// <c>IdnpTests.TryCreate_WithValidChecksum_Succeeds</c>.
    /// </summary>
    private const string ValidIdnp = "2000123456782";

    /// <summary>Builds a known-good input that callers can mutate per test.</summary>
    private static InsuredPersonRegistrationInput BuildValid(
        string idnp = ValidIdnp,
        string lastName = "Popescu",
        string firstName = "Ion",
        string? patronymic = "Vasilevici",
        DateOnly? birthDate = null)
        => new(idnp, lastName, firstName, patronymic, birthDate ?? new DateOnly(1980, 5, 12));

    [Fact]
    public void Valid_Input_PassesAllRules()
    {
        var input = BuildValid();

        var result = _validator.TestValidate(input);

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Idnp_Empty_Fails()
    {
        var input = BuildValid(idnp: string.Empty);

        var result = _validator.TestValidate(input);

        result.ShouldHaveValidationErrorFor(x => x.Idnp);
    }

    [Fact]
    public void Idnp_WrongLength_Fails()
    {
        // Arrange: 12 digits — fails the [012][0-9]{12} pattern in Idnp.
        var input = BuildValid(idnp: "200012345678");

        var result = _validator.TestValidate(input);

        result.ShouldHaveValidationErrorFor(x => x.Idnp);
    }

    [Fact]
    public void Idnp_BadChecksum_Fails()
    {
        // Arrange: 13 digits, right century prefix, but last digit deliberately wrong.
        var bad = ValidIdnp[..12] + (ValidIdnp[12] == '0' ? '1' : '0');
        var input = BuildValid(idnp: bad);

        var result = _validator.TestValidate(input);

        result.ShouldHaveValidationErrorFor(x => x.Idnp);
    }

    [Fact]
    public void LastName_Empty_Fails()
    {
        var input = BuildValid(lastName: string.Empty);

        var result = _validator.TestValidate(input);

        result.ShouldHaveValidationErrorFor(x => x.LastName);
    }

    [Fact]
    public void LastName_TooLong_Fails()
    {
        // Arrange: 101 chars — one above the 100 ceiling.
        var input = BuildValid(lastName: new string('a', 101));

        var result = _validator.TestValidate(input);

        result.ShouldHaveValidationErrorFor(x => x.LastName);
    }

    [Fact]
    public void LastName_OnlyDigits_Fails()
    {
        // Arrange: digits only — fails the "at least one letter" guard.
        var input = BuildValid(lastName: "1234567");

        var result = _validator.TestValidate(input);

        result.ShouldHaveValidationErrorFor(x => x.LastName);
    }

    [Fact]
    public void FirstName_Empty_Fails()
    {
        var input = BuildValid(firstName: string.Empty);

        var result = _validator.TestValidate(input);

        result.ShouldHaveValidationErrorFor(x => x.FirstName);
    }

    [Fact]
    public void BirthDate_InTheFuture_Fails()
    {
        // Arrange: 100 years in the future relative to the fake clock's "today".
        var input = BuildValid(birthDate: Today.AddYears(100));

        var result = _validator.TestValidate(input);

        result.ShouldHaveValidationErrorFor(x => x.BirthDate);
    }
}
