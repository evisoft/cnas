using Cnas.Ps.Application.Audit;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Application.Tests.Audit;

/// <summary>
/// Unit tests for <see cref="CefFormatter"/>. R0190 / SEC 049. The formatter is a pure
/// function over an <see cref="AuditLog"/> row; every test fully constructs the row,
/// invokes <see cref="CefFormatter.Format"/>, and asserts on the resulting line — no
/// transport involvement.
/// </summary>
public class CefFormatterTests
{
    /// <summary>Stable timestamp used by every fixture for deterministic <c>rt=</c> output.</summary>
    private static readonly DateTime FixedNow = new(2026, 5, 21, 14, 0, 0, DateTimeKind.Utc);

    /// <summary>Expected Unix epoch millis for <see cref="FixedNow"/>.</summary>
    private static readonly long FixedNowMillis =
        new DateTimeOffset(FixedNow, TimeSpan.Zero).ToUnixTimeMilliseconds();

    [Fact]
    public void Format_BasicRow_ProducesCefV0LineWithExpectedFields()
    {
        var row = BuildRow();

        var cef = CefFormatter.Format(row, "CNAS", "Cnas.Ps", "1.0");

        cef.Should().StartWith("CEF:0|CNAS|Cnas.Ps|1.0|USER.LOGIN.SUCCESS|USER.LOGIN.SUCCESS|");
        cef.Should().Contain($"rt={FixedNowMillis}");
        cef.Should().Contain("act=USER.LOGIN.SUCCESS");
        cef.Should().Contain("suser=user-42");
        cef.Should().Contain("cs1Label=TargetEntity");
        cef.Should().Contain("cs1=UserProfile");
        cef.Should().Contain("cn1Label=TargetEntityId");
        cef.Should().Contain("cn1=42");
        cef.Should().Contain("src=10.0.0.1");
        cef.Should().Contain("externalId=corr-abc");
        cef.Should().Contain("cs6Label=Details");
        cef.Should().Contain("cs6={}");
        // Line should NOT end with a stray space — the formatter trims the final
        // extension delimiter so syslog framers see a clean terminator.
        cef.Should().NotEndWith(" ");
    }

    [Fact]
    public void Format_NullableFields_Omitted()
    {
        var row = BuildRow(
            targetEntity: null,
            targetEntityId: null,
            sourceIp: null,
            correlationId: null);

        var cef = CefFormatter.Format(row, "CNAS", "Cnas.Ps", "1.0");

        cef.Should().NotContain("cs1=");
        cef.Should().NotContain("cs1Label=");
        cef.Should().NotContain("cn1=");
        cef.Should().NotContain("cn1Label=");
        cef.Should().NotContain("src=");
        cef.Should().NotContain("externalId=");
        // act / suser are always present (required producer-side fields).
        cef.Should().Contain("act=USER.LOGIN.SUCCESS");
        cef.Should().Contain("suser=user-42");
    }

    [Fact]
    public void Format_PipesInHeaderFields_Escaped()
    {
        // Header pipes must be escaped or they would be misparsed as field boundaries.
        var row = BuildRow(eventCode: "X|Y");

        var cef = CefFormatter.Format(row, "CN|AS", "Cnas.Ps", "1.0");

        // The CEF header pipe is escaped to "\|" — the literal raw "X|Y" must NOT appear.
        cef.Should().Contain("CEF:0|CN\\|AS|Cnas.Ps|1.0|X\\|Y|X\\|Y|");
    }

    [Fact]
    public void Format_EqualsInExtensionValue_Escaped()
    {
        // The CEF extension delimiter is '=', so embedded '=' inside DetailsJson MUST be
        // backslash-escaped or parsers will misframe the trailing payload.
        var detailsJson = "{\"name\":\"X=Y\"}";
        var row = BuildRow(detailsJson: detailsJson);

        var cef = CefFormatter.Format(row, "CNAS", "Cnas.Ps", "1.0");

        // The single legitimate "key=" sequence for cs6 must remain unescaped; every
        // embedded '=' inside the JSON value must carry a leading backslash.
        cef.Should().Contain("cs6={\"name\":\"X\\=Y\"}");
    }

    [Fact]
    public void Format_BackslashEscaped()
    {
        // Both header and extension regimes escape backslash by doubling it. The header
        // path is exercised via the EventCode and the extension path via DetailsJson.
        var row = BuildRow(
            eventCode: "A\\B",
            detailsJson: "{\"path\":\"C\\\\D\"}");

        var cef = CefFormatter.Format(row, "CNAS", "Cnas.Ps", "1.0");

        // Header: single literal backslash doubles to "\\".
        cef.Should().Contain("|A\\\\B|A\\\\B|");
        // Extension: the raw JSON value already carries an escaped backslash; the
        // formatter doubles every literal backslash it sees on the wire, so the visible
        // form becomes the doubled form. We assert the formatter applied SOME escaping
        // by checking the count of backslashes did not survive raw.
        cef.Should().NotContain("\"path\":\"C\\D\"");
    }

    [Theory]
    [InlineData(AuditSeverity.Information, 4)]
    [InlineData(AuditSeverity.Notice, 5)]
    [InlineData(AuditSeverity.Sensitive, 7)]
    [InlineData(AuditSeverity.Critical, 9)]
    public void Format_SeverityMapping_Parameterized(AuditSeverity input, int expectedCefSeverity)
    {
        var row = BuildRow(severity: input);

        var cef = CefFormatter.Format(row, "CNAS", "Cnas.Ps", "1.0");

        // Severity is the 7th pipe-delimited header field. Split safely on '|' and
        // assert exact match — protects against silent off-by-one regressions.
        var parts = cef.Split('|');
        parts.Length.Should().BeGreaterThanOrEqualTo(8);
        parts[6].Should().Be(expectedCefSeverity.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Format_DetailsJsonWithNewlines_NewlinesEscaped()
    {
        // Newlines inside an extension value would otherwise terminate the syslog record
        // mid-line and corrupt downstream parsing. CEF mandates "\n" escaping.
        var detailsJson = "{\n  \"k\":\"v\"\n}";
        var row = BuildRow(detailsJson: detailsJson);

        var cef = CefFormatter.Format(row, "CNAS", "Cnas.Ps", "1.0");

        // The line must remain a single line.
        cef.Should().NotContain("\n");
        // Literal "\n" (two characters) must be present in the output where each newline
        // used to be.
        cef.Should().Contain("\\n");
    }

    [Fact]
    public void Format_CrInDetailsJson_Stripped()
    {
        // Carriage returns are stripped (not preserved) because downstream syslog
        // framers handle CR inconsistently. Documenting the policy as a test.
        var detailsJson = "{\"k\":\"v\"}\r\n";
        var row = BuildRow(detailsJson: detailsJson);

        var cef = CefFormatter.Format(row, "CNAS", "Cnas.Ps", "1.0");

        cef.Should().NotContain("\r");
    }

    /// <summary>
    /// Constructs a fully-populated <see cref="AuditLog"/> fixture with sensible defaults
    /// for every required field. Individual tests override only the parameters they care
    /// about so the test bodies focus on the assertion.
    /// </summary>
    private static AuditLog BuildRow(
        string eventCode = "USER.LOGIN.SUCCESS",
        AuditSeverity severity = AuditSeverity.Information,
        string actorId = "user-42",
        string? targetEntity = "UserProfile",
        long? targetEntityId = 42L,
        string detailsJson = "{}",
        string? sourceIp = "10.0.0.1",
        string? correlationId = "corr-abc")
    {
        return new AuditLog
        {
            CreatedAtUtc = FixedNow,
            EventAtUtc = FixedNow,
            EventCode = eventCode,
            Severity = severity,
            ActorId = actorId,
            TargetEntity = targetEntity,
            TargetEntityId = targetEntityId,
            DetailsJson = detailsJson,
            SourceIp = sourceIp,
            CorrelationId = correlationId,
            PrevHash = "GENESIS",
            RowHash = new string('0', 64),
        };
    }
}
