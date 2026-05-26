using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Core.Tests.ValueObjects;

public class IdnpTests
{
    // The Moldovan IDNP checksum uses weights {7, 3, 1} cycling over the first 12 digits,
    // then check digit = (10 - (sum % 10)) % 10.
    private static string BuildIdnpWithChecksum(string twelveDigitPrefix)
    {
        if (twelveDigitPrefix.Length != 12)
            throw new ArgumentException("Prefix must be 12 digits.", nameof(twelveDigitPrefix));

        int[] weights = { 7, 3, 1 };
        int sum = 0;
        for (int i = 0; i < 12; i++)
            sum += (twelveDigitPrefix[i] - '0') * weights[i % 3];

        int check = (10 - (sum % 10)) % 10;
        return twelveDigitPrefix + check.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public void TryCreate_WithValidChecksum_Succeeds()
    {
        var idnp = BuildIdnpWithChecksum("200012345678");

        var result = Idnp.TryCreate(idnp);

        result.IsSuccess.Should().BeTrue();
        result.Value.Value.Should().Be(idnp);
    }

    [Fact]
    public void TryCreate_PrefixStartingWithZero_Succeeds()
    {
        var idnp = BuildIdnpWithChecksum("019951122334");

        var result = Idnp.TryCreate(idnp);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void TryCreate_PrefixStartingWithOne_Succeeds()
    {
        var idnp = BuildIdnpWithChecksum("119880506122");

        var result = Idnp.TryCreate(idnp);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void TryCreate_AllZeros_Fails()
    {
        var result = Idnp.TryCreate("0000000000000");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidIdnp);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("   ")]
    public void TryCreate_NullOrWhitespace_Fails(string input)
    {
        var result = Idnp.TryCreate(input);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidIdnp);
    }

    [Fact]
    public void TryCreate_Null_Fails()
    {
        var result = Idnp.TryCreate(null!);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidIdnp);
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("12345678901234")]
    [InlineData("123456789012")]
    public void TryCreate_WrongLength_Fails(string input)
    {
        var result = Idnp.TryCreate(input);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidIdnp);
    }

    [Theory]
    [InlineData("3000000000000")]
    [InlineData("9000000000000")]
    public void TryCreate_FirstDigitNotZeroOneOrTwo_Fails(string input)
    {
        var result = Idnp.TryCreate(input);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidIdnp);
    }

    [Fact]
    public void TryCreate_NonDigit_Fails()
    {
        var result = Idnp.TryCreate("20001234567Z8");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidIdnp);
    }

    [Fact]
    public void TryCreate_BadChecksum_Fails()
    {
        // Build a valid IDNP, then mutate the last digit to break the checksum.
        var good = BuildIdnpWithChecksum("200012345678");
        int lastDigit = good[12] - '0';
        int badDigit = (lastDigit + 1) % 10;
        var bad = good[..12] + badDigit.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var result = Idnp.TryCreate(bad);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidIdnp);
    }

    [Fact]
    public void TryCreate_TrimsLeadingTrailingWhitespace()
    {
        var idnp = BuildIdnpWithChecksum("200012345678");

        var result = Idnp.TryCreate("  " + idnp + "  ");

        result.IsSuccess.Should().BeTrue();
        result.Value.Value.Should().Be(idnp);
    }

    [Fact]
    public void Equality_TwoIdnpsWithSameValue_AreEqual()
    {
        var idnp = BuildIdnpWithChecksum("200012345678");

        var a = Idnp.TryCreate(idnp).Value;
        var b = Idnp.TryCreate(idnp).Value;

        a.Should().Be(b);
        (a == b).Should().BeTrue();
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void ToString_ReturnsCanonicalForm()
    {
        var idnp = BuildIdnpWithChecksum("200012345678");

        var value = Idnp.TryCreate(idnp).Value;

        value.ToString().Should().Be(idnp);
    }
}
