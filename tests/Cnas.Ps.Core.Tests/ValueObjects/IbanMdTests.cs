using System.Globalization;
using System.Numerics;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Core.Tests.ValueObjects;

public class IbanMdTests
{
    /// <summary>
    /// Builds a syntactically valid Moldovan IBAN from a 20-character alphanumeric BBAN,
    /// computing the ISO 13616 mod-97 check digits.
    /// </summary>
    private static string BuildMoldovanIban(string twentyCharBban)
    {
        if (twentyCharBban.Length != 20)
            throw new ArgumentException("BBAN must be 20 characters.", nameof(twentyCharBban));

        // Compute check: build a string of bban + 'MD' + '00', map letters to numbers (A=10,...),
        // take mod 97, then check = 98 - mod.
        string rearranged = twentyCharBban + "MD00";
        string numeric = LettersToNumbers(rearranged);
        BigInteger value = BigInteger.Parse(numeric, CultureInfo.InvariantCulture);
        int mod = (int)(value % 97);
        int check = 98 - mod;
        return "MD" + check.ToString("D2", CultureInfo.InvariantCulture) + twentyCharBban;
    }

    private static string LettersToNumbers(string input)
    {
        var sb = new System.Text.StringBuilder(input.Length * 2);
        foreach (char c in input)
        {
            if (char.IsDigit(c))
                sb.Append(c);
            else if (c is >= 'A' and <= 'Z')
                sb.Append((c - 'A' + 10).ToString(CultureInfo.InvariantCulture));
            else
                throw new ArgumentException("Unexpected character.", nameof(input));
        }
        return sb.ToString();
    }

    [Fact]
    public void TryCreate_ValidMoldovanIban_Succeeds()
    {
        var iban = BuildMoldovanIban("AG00000000123456789X");

        var result = IbanMd.TryCreate(iban);

        result.IsSuccess.Should().BeTrue();
        result.Value.Value.Should().Be(iban);
    }

    [Fact]
    public void TryCreate_LowercaseInput_NormalisesToUpper()
    {
        var iban = BuildMoldovanIban("AG00000000123456789X");

        var result = IbanMd.TryCreate(iban.ToLowerInvariant());

        result.IsSuccess.Should().BeTrue();
        result.Value.Value.Should().Be(iban);
    }

    [Fact]
    public void TryCreate_WithEmbeddedSpaces_StripsAndAccepts()
    {
        var iban = BuildMoldovanIban("AG00000000123456789X");
        var spaced = string.Join(' ', Chunk(iban, 4));

        var result = IbanMd.TryCreate(spaced);

        result.IsSuccess.Should().BeTrue();
        result.Value.Value.Should().Be(iban);
    }

    private static IEnumerable<string> Chunk(string s, int size)
    {
        for (int i = 0; i < s.Length; i += size)
            yield return s.Substring(i, Math.Min(size, s.Length - i));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryCreate_NullOrEmpty_Fails(string? input)
    {
        var result = IbanMd.TryCreate(input!);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidIban);
    }

    [Fact]
    public void TryCreate_WrongCountryCode_Fails()
    {
        // Valid-looking IBAN but for RO (Romania), not MD.
        var result = IbanMd.TryCreate("RO49AAAA1B31007593840000");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidIban);
    }

    [Fact]
    public void TryCreate_WrongLength_Fails()
    {
        var result = IbanMd.TryCreate("MD24AG0000000000000000");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidIban);
    }

    [Fact]
    public void TryCreate_BadChecksum_Fails()
    {
        var good = BuildMoldovanIban("AG00000000123456789X");
        // Flip one of the check digits to invalidate the checksum.
        int c2 = good[2] - '0';
        int c3 = good[3] - '0';
        int newC3 = (c3 + 1) % 10;
        var bad = good[..2]
            + c2.ToString(CultureInfo.InvariantCulture)
            + newC3.ToString(CultureInfo.InvariantCulture)
            + good[4..];

        var result = IbanMd.TryCreate(bad);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidIban);
    }

    [Fact]
    public void TryCreate_NonAlphanumeric_Fails()
    {
        var result = IbanMd.TryCreate("MD24AG000000012345!789X");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidIban);
    }

    [Fact]
    public void Equality_SameIban_AreEqual()
    {
        var iban = BuildMoldovanIban("AG00000000123456789X");

        var a = IbanMd.TryCreate(iban).Value;
        var b = IbanMd.TryCreate(iban).Value;

        a.Should().Be(b);
        (a == b).Should().BeTrue();
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void ToString_ReturnsCanonicalForm()
    {
        var iban = BuildMoldovanIban("AG00000000123456789X");

        var value = IbanMd.TryCreate(iban).Value;

        value.ToString().Should().Be(iban);
    }
}
