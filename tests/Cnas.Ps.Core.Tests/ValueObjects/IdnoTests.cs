using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Core.Tests.ValueObjects;

public class IdnoTests
{
    // Same weighted-checksum algorithm as IDNP (weights 7,3,1 cycling, mod 10).
    private static string BuildIdnoWithChecksum(string twelveDigitPrefix)
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
        var idno = BuildIdnoWithChecksum("100355012345");

        var result = Idno.TryCreate(idno);

        result.IsSuccess.Should().BeTrue();
        result.Value.Value.Should().Be(idno);
    }

    [Fact]
    public void TryCreate_FirstDigitNine_Succeeds()
    {
        var idno = BuildIdnoWithChecksum("912345678901");

        var result = Idno.TryCreate(idno);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void TryCreate_FirstDigitZero_Fails()
    {
        var idno = BuildIdnoWithChecksum("012345678901");

        var result = Idno.TryCreate(idno);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidIdno);
    }

    [Fact]
    public void TryCreate_AllZeros_Fails()
    {
        var result = Idno.TryCreate("0000000000000");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidIdno);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void TryCreate_NullOrWhitespace_Fails(string input)
    {
        var result = Idno.TryCreate(input);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidIdno);
    }

    [Fact]
    public void TryCreate_Null_Fails()
    {
        var result = Idno.TryCreate(null!);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidIdno);
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("12345678901234")]
    public void TryCreate_WrongLength_Fails(string input)
    {
        var result = Idno.TryCreate(input);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidIdno);
    }

    [Fact]
    public void TryCreate_NonDigit_Fails()
    {
        var result = Idno.TryCreate("10035501234X5");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidIdno);
    }

    [Fact]
    public void TryCreate_BadChecksum_Fails()
    {
        var good = BuildIdnoWithChecksum("100355012345");
        int lastDigit = good[12] - '0';
        int badDigit = (lastDigit + 1) % 10;
        var bad = good[..12] + badDigit.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var result = Idno.TryCreate(bad);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidIdno);
    }

    [Fact]
    public void Equality_TwoIdnosWithSameValue_AreEqual()
    {
        var idno = BuildIdnoWithChecksum("100355012345");

        var a = Idno.TryCreate(idno).Value;
        var b = Idno.TryCreate(idno).Value;

        a.Should().Be(b);
        (a == b).Should().BeTrue();
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void ToString_ReturnsCanonicalForm()
    {
        var idno = BuildIdnoWithChecksum("100355012345");

        var value = Idno.TryCreate(idno).Value;

        value.ToString().Should().Be(idno);
    }
}
