using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Core.Tests.ValueObjects;

public class PhoneE164Tests
{
    [Fact]
    public void TryCreate_ValidMoldovanMobile_Succeeds()
    {
        var result = PhoneE164.TryCreate("+37322255555");

        result.IsSuccess.Should().BeTrue();
        result.Value.Value.Should().Be("+37322255555");
    }

    [Theory]
    [InlineData("+373 22 255 555")]
    [InlineData("+373-22-255-555")]
    [InlineData("+373 (22) 255-555")]
    [InlineData(" +37322255555 ")]
    public void TryCreate_FormattingVariants_NormaliseToCanonical(string input)
    {
        var result = PhoneE164.TryCreate(input);

        result.IsSuccess.Should().BeTrue();
        result.Value.Value.Should().Be("+37322255555");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void TryCreate_NullOrEmpty_Fails(string? input)
    {
        var result = PhoneE164.TryCreate(input!);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidPhone);
    }

    [Fact]
    public void TryCreate_MissingPlus_Fails()
    {
        var result = PhoneE164.TryCreate("37322255555");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidPhone);
    }

    [Fact]
    public void TryCreate_LeadingZeroAfterPlus_Fails()
    {
        var result = PhoneE164.TryCreate("+037322255555");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidPhone);
    }

    [Fact]
    public void TryCreate_TooLong_Fails()
    {
        // E.164 max = 15 digits after '+'
        var result = PhoneE164.TryCreate("+1234567890123456");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidPhone);
    }

    [Fact]
    public void TryCreate_TooShort_Fails()
    {
        // E.164 minimum = 2 digits (one country, one subscriber).
        var result = PhoneE164.TryCreate("+3");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidPhone);
    }

    [Fact]
    public void TryCreate_ContainsLetters_Fails()
    {
        var result = PhoneE164.TryCreate("+3732AA255555");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidPhone);
    }

    [Fact]
    public void Equality_SameNumber_AreEqual()
    {
        var a = PhoneE164.TryCreate("+37322255555").Value;
        var b = PhoneE164.TryCreate("+373 222 555 55").Value;

        a.Should().Be(b);
        (a == b).Should().BeTrue();
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void ToString_ReturnsCanonicalForm()
    {
        var p = PhoneE164.TryCreate("+373 22 255 555").Value;

        p.ToString().Should().Be("+37322255555");
    }
}
