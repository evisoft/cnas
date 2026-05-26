using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Tests.Decisions;

/// <summary>
/// Behaviour tests for <see cref="FormPayloadParser"/>. Covers JSON → fact-bag conversion
/// for every supported scalar type plus the malformed/non-object failure paths.
/// </summary>
public class FormPayloadParserTests
{
    private static readonly DateTime ClaimDate =
        new(2026, 5, 19, 10, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void Parse_InvalidJson_ReturnsBadRule()
    {
        var result = FormPayloadParser.Parse("not json at all {", ClaimDate);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.BadRule);
    }

    [Fact]
    public void Parse_JsonArray_ReturnsBadRule()
    {
        var result = FormPayloadParser.Parse("[1,2,3]", ClaimDate);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.BadRule);
        result.ErrorMessage.Should().Contain("object");
    }

    [Fact]
    public void Parse_EmptyObject_AddsClaimDateOnly()
    {
        var result = FormPayloadParser.Parse("{}", ClaimDate);

        result.IsSuccess.Should().BeTrue();
        result.Value.Values.Should().ContainSingle();
        result.Value.Values["claimDateUtc"].Should().Be(ClaimDate);
    }

    [Fact]
    public void Parse_WithBoolField_PreservesBool()
    {
        var result = FormPayloadParser.Parse("""{"isInsured": true}""", ClaimDate);

        result.IsSuccess.Should().BeTrue();
        result.Value.Values["isInsured"].Should().Be(true);
        result.Value.Values["isInsured"].Should().BeOfType<bool>();
    }

    [Fact]
    public void Parse_WithIntegerField_StoresAsLong()
    {
        var result = FormPayloadParser.Parse("""{"birthOrder": 2}""", ClaimDate);

        result.IsSuccess.Should().BeTrue();
        result.Value.Values["birthOrder"].Should().BeOfType<long>();
        result.Value.Values["birthOrder"].Should().Be(2L);
    }

    [Fact]
    public void Parse_WithDecimalField_StoresAsDecimal()
    {
        var result = FormPayloadParser.Parse("""{"salary": 8500.75}""", ClaimDate);

        result.IsSuccess.Should().BeTrue();
        result.Value.Values["salary"].Should().BeOfType<decimal>();
        result.Value.Values["salary"].Should().Be(8500.75m);
    }

    [Fact]
    public void Parse_WithIsoDateField_StoresAsUtcDateTime()
    {
        var result = FormPayloadParser.Parse(
            """{"birthDateUtc": "2026-01-15T00:00:00Z"}""",
            ClaimDate);

        result.IsSuccess.Should().BeTrue();
        result.Value.Values["birthDateUtc"].Should().BeOfType<DateTime>();
        var dt = (DateTime)result.Value.Values["birthDateUtc"]!;
        dt.Kind.Should().Be(DateTimeKind.Utc);
        dt.Should().Be(new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Parse_ExistingClaimDateUtcInPayload_NotOverwritten()
    {
        var payloadClaim = new DateTime(2025, 12, 1, 9, 0, 0, DateTimeKind.Utc);
        var result = FormPayloadParser.Parse(
            """{"claimDateUtc": "2025-12-01T09:00:00Z"}""",
            ClaimDate);

        result.IsSuccess.Should().BeTrue();
        result.Value.Values["claimDateUtc"].Should().Be(payloadClaim);
        // ClaimDate fallback (2026-05-19) must NOT have replaced the payload value.
        ((DateTime)result.Value.Values["claimDateUtc"]!).Should().NotBe(ClaimDate);
    }

    [Fact]
    public void Parse_PlainStringField_StoresAsString()
    {
        var result = FormPayloadParser.Parse(
            """{"parentIdnp": "2000000000007"}""",
            ClaimDate);

        result.IsSuccess.Should().BeTrue();
        result.Value.Values["parentIdnp"].Should().BeOfType<string>();
        result.Value.Values["parentIdnp"].Should().Be("2000000000007");
    }

    [Fact]
    public void Parse_NestedArraysAndObjects_AreIgnored()
    {
        var result = FormPayloadParser.Parse(
            """{"children":[1,2], "address":{"city":"Chisinau"}, "isInsured": true}""",
            ClaimDate);

        result.IsSuccess.Should().BeTrue();
        result.Value.Values.Should().NotContainKey("children");
        result.Value.Values.Should().NotContainKey("address");
        result.Value.Values["isInsured"].Should().Be(true);
    }
}
