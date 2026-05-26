using Cnas.Ps.Application.Audit;

namespace Cnas.Ps.Application.Tests.Audit;

/// <summary>
/// Unit tests for <see cref="PiiRedactor"/>. SEC 044 / CLAUDE.md §5.6 requires audit
/// payloads to never persist PII; the redactor is the single enforcement point applied
/// by <c>AuditService</c> at the write boundary.
/// </summary>
public class PiiRedactorTests
{
    [Fact]
    public void EmptyObject_RoundTrips()
    {
        var output = PiiRedactor.Redact("{}");

        output.Should().Be("{}");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NullOrWhitespaceInput_ReturnsEmptyObject(string? input)
    {
        var output = PiiRedactor.Redact(input);

        output.Should().Be("{}");
    }

    [Fact]
    public void MalformedJson_ReturnsInvalidJsonMarker()
    {
        var output = PiiRedactor.Redact("this is not json {{");

        output.Should().Be("{\"_invalidJson\":\"[redacted]\"}");
    }

    [Fact]
    public void SimplePiiKey_IsRedacted()
    {
        var output = PiiRedactor.Redact("""{"idnp":"2000123456782"}""");

        output.Should().Be("""{"idnp":"[redacted]"}""");
    }

    [Fact]
    public void CaseInsensitiveKeyMatch()
    {
        var output = PiiRedactor.Redact("""{"Email":"alice@example.md"}""");

        output.Should().Be("""{"Email":"[redacted]"}""");
    }

    [Fact]
    public void SubstringKeyMatch()
    {
        var output = PiiRedactor.Redact("""{"userEmail":"alice@example.md"}""");

        output.Should().Be("""{"userEmail":"[redacted]"}""");
    }

    [Fact]
    public void NonPiiKey_PassesThrough()
    {
        var json = """{"applicationId":"a1b2","status":"Submitted"}""";

        var output = PiiRedactor.Redact(json);

        output.Should().Be(json);
    }

    [Fact]
    public void NestedPiiKey_InObject_IsRedacted()
    {
        var output = PiiRedactor.Redact("""{"meta":{"password":"hunter2","status":"ok"}}""");

        output.Should().Be("""{"meta":{"password":"[redacted]","status":"ok"}}""");
    }

    [Fact]
    public void PiiKey_InArray_IsRedactedForEachItem()
    {
        var output = PiiRedactor.Redact(
            """{"recipients":[{"email":"a@b.md"},{"email":"c@d.md"}]}""");

        output.Should().Be(
            """{"recipients":[{"email":"[redacted]"},{"email":"[redacted]"}]}""");
    }

    [Fact]
    public void MixedSafeAndPii_OnlyPiiRedacted()
    {
        var output = PiiRedactor.Redact(
            """{"applicationId":"a1b2","idnp":"2000123456782","status":"Submitted"}""");

        output.Should().Be(
            """{"applicationId":"a1b2","idnp":"[redacted]","status":"Submitted"}""");
    }

    [Fact]
    public void IntegerKeyValues_PreservedWhenNotPii()
    {
        var json = """{"count":42}""";

        var output = PiiRedactor.Redact(json);

        output.Should().Be(json);
    }

    [Theory]
    [InlineData("idnp")]
    [InlineData("idno")]
    [InlineData("cnp")]
    [InlineData("password")]
    [InlineData("passwd")]
    [InlineData("pwd")]
    [InlineData("secret")]
    [InlineData("token")]
    [InlineData("apikey")]
    [InlineData("api_key")]
    [InlineData("email")]
    [InlineData("phone")]
    [InlineData("msisdn")]
    [InlineData("mobile")]
    [InlineData("pin")]
    [InlineData("signingkey")]
    [InlineData("signing_key")]
    [InlineData("iban")]
    [InlineData("bankaccount")]
    [InlineData("bank_account")]
    [InlineData("accountnumber")]
    [InlineData("account_number")]
    public void MultipleAliases_AllRedactedToPlaceholder(string key)
    {
        var json = $"{{\"{key}\":\"sensitive\"}}";

        var output = PiiRedactor.Redact(json);

        output.Should().Be($"{{\"{key}\":\"[redacted]\"}}");
    }
}
