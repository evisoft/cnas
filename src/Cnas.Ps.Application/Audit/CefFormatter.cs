using System.Globalization;
using System.Text;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Application.Audit;

/// <summary>
/// Static helper that formats a single <see cref="AuditLog"/> row into an ArcSight CEF
/// (Common Event Format) line. R0190 / SEC 049.
/// </summary>
/// <remarks>
/// <para>
/// <b>Wire shape.</b> A CEF record is a single ASCII line of the form
/// <c>CEF:0|Vendor|Product|Version|Signature|Name|Severity|Extension</c> — seven
/// pipe-delimited header fields followed by a space-delimited <c>key=value</c>
/// extension block. The <c>"CEF:0"</c> prefix is the protocol-version literal.
/// </para>
/// <para>
/// <b>Escaping discipline.</b> The CEF spec requires two distinct escape regimes:
/// <list type="bullet">
///   <item><description>Header fields (everything before <c>Extension</c>) must escape the literal pipe character (<c>|</c>) and backslash (<c>\</c>).</description></item>
///   <item><description>Extension values must escape the equals sign (<c>=</c>), backslash, and embedded newlines (so a multi-line JSON payload survives transport).</description></item>
/// </list>
/// The two escape regimes are NOT interchangeable — applying header escaping to an
/// extension value would silently corrupt the <c>=</c>-bearing JSON payload, and vice
/// versa. The helpers below are deliberately kept private so callers cannot misroute.
/// </para>
/// <para>
/// <b>Severity mapping.</b> CEF severity is an integer in <c>[0, 10]</c>. The
/// <see cref="AuditSeverity"/> enum maps as follows:
/// <see cref="AuditSeverity.Information"/> → 4 (informational),
/// <see cref="AuditSeverity.Notice"/> → 5 (notice / write to non-sensitive data),
/// <see cref="AuditSeverity.Sensitive"/> → 7 (sensitive-data access — equivalent to "warning" in CEF),
/// <see cref="AuditSeverity.Critical"/> → 9 (security-relevant change — equivalent to "error" in CEF).
/// The mapping is intentionally non-saturated (no <c>10</c>) to leave headroom for a future
/// emergency level.
/// </para>
/// <para>
/// <b>PII contract.</b> The formatter passes <see cref="AuditLog.DetailsJson"/> verbatim
/// into the <c>cs6</c> custom-string extension — the JSON is already PII-redacted at the
/// audit-write boundary (R0185 / SEC 044), so the formatter never re-touches its contents.
/// No additional PII surface is introduced by this layer.
/// </para>
/// </remarks>
public static class CefFormatter
{
    /// <summary>
    /// Formats a single <see cref="AuditLog"/> row into a CEF line WITHOUT a trailing
    /// newline. The caller (typically the syslog transport adapter) is responsible for
    /// wrapping the line in an RFC 5424 syslog header and appending the framing newline.
    /// </summary>
    /// <param name="row">Persisted audit row to format. Must not be <c>null</c>.</param>
    /// <param name="vendor">CEF vendor header field (e.g. <c>"CNAS"</c>).</param>
    /// <param name="product">CEF product header field (e.g. <c>"Cnas.Ps"</c>).</param>
    /// <param name="version">CEF product-version header field (e.g. <c>"1.0"</c>).</param>
    /// <returns>The fully-escaped CEF record, without trailing newline.</returns>
    public static string Format(AuditLog row, string vendor, string product, string version)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(vendor);
        ArgumentNullException.ThrowIfNull(product);
        ArgumentNullException.ThrowIfNull(version);

        // Header fields: signature + human-readable name. For now they are the same;
        // i18n of Name is out of scope until we have a translation pipeline (see R0190
        // future scope).
        var signature = EscapeHeader(row.EventCode);
        var name = EscapeHeader(row.EventCode);
        var severity = MapSeverity(row.Severity);

        var ext = new StringBuilder(capacity: 256);

        // rt — event reception time in Unix epoch milliseconds (UTC). CEF parsers treat
        // this as the authoritative event time, taking precedence over the syslog header
        // timestamp which represents the relay/forward instant.
        AppendExtension(ext, "rt",
            new DateTimeOffset(row.EventAtUtc, TimeSpan.Zero)
                .ToUnixTimeMilliseconds()
                .ToString(CultureInfo.InvariantCulture));

        // act — action / event code. Duplicates the signature so SIEM rules that key off
        // either field continue to work without bespoke parser glue.
        AppendExtension(ext, "act", row.EventCode);

        // suser — source user identifier. The audit subsystem stores an opaque ActorId
        // (Sqid for humans, literal "system" for jobs); no PII per SEC 042.
        AppendExtension(ext, "suser", row.ActorId);

        // cs1 / cs1Label — target entity kind (Application, Dossier, ...). The matching
        // *Label field is mandated by CEF so the SIEM UI can render a column header.
        if (row.TargetEntity is not null)
        {
            AppendExtension(ext, "cs1Label", "TargetEntity");
            AppendExtension(ext, "cs1", row.TargetEntity);
        }

        // cn1 / cn1Label — target entity primary key. Numeric so SIEM filters can range-
        // query. Always invariant culture so a decimal-comma locale never produces
        // "1,234" instead of "1234".
        if (row.TargetEntityId is not null)
        {
            AppendExtension(ext, "cn1Label", "TargetEntityId");
            AppendExtension(ext, "cn1",
                row.TargetEntityId.Value.ToString(CultureInfo.InvariantCulture));
        }

        // src — source IP. Stored as the textual form (IPv4 or IPv6) by the audit
        // subsystem; CEF accepts either.
        if (row.SourceIp is not null)
        {
            AppendExtension(ext, "src", row.SourceIp);
        }

        // externalId — request / job correlation id. Reuses the CEF reserved key so
        // dashboards built on existing CEF schemas pick it up without configuration.
        if (row.CorrelationId is not null)
        {
            AppendExtension(ext, "externalId", row.CorrelationId);
        }

        // cs6 / cs6Label — opaque structured details JSON. Already PII-redacted upstream
        // (R0185); we only need to apply CEF extension escaping for embedded '=' and
        // newlines (the JSON spec allows neither inside string values, but the JSON
        // structural '=' between fields is the concern). The Label is included so SIEM
        // operators see "Details" rather than "cs6" in their column picker.
        AppendExtension(ext, "cs6Label", "Details");
        AppendExtension(ext, "cs6", row.DetailsJson);

        // Build the final line. Trailing space from the last AppendExtension call is
        // trimmed so parsers that rely on a clean line terminator do not see a stray
        // delimiter.
        return string.Create(
            CultureInfo.InvariantCulture,
            $"CEF:0|{EscapeHeader(vendor)}|{EscapeHeader(product)}|{EscapeHeader(version)}|{signature}|{name}|{severity}|{ext.ToString().TrimEnd()}");
    }

    /// <summary>
    /// Maps an <see cref="AuditSeverity"/> to its CEF severity integer (range <c>[0, 10]</c>).
    /// See the class-level remarks for the rationale of each landing point.
    /// </summary>
    /// <param name="severity">Audit-subsystem severity classification.</param>
    /// <returns>Integer in <c>[4, 9]</c>; unrecognised values land at <c>4</c>.</returns>
    public static int MapSeverity(AuditSeverity severity) => severity switch
    {
        AuditSeverity.Information => 4,
        AuditSeverity.Notice => 5,
        AuditSeverity.Sensitive => 7,
        AuditSeverity.Critical => 9,
        _ => 4,
    };

    /// <summary>
    /// Escapes a header field per the CEF spec: backslash and pipe become
    /// <c>\\</c> and <c>\|</c> respectively. Header values must NEVER carry embedded
    /// newlines; the audit subsystem guarantees this on the producer side.
    /// </summary>
    /// <param name="value">Raw header field value.</param>
    /// <returns>Header-escaped string suitable for the pipe-delimited header.</returns>
    private static string EscapeHeader(string value)
    {
        // Backslash is escaped FIRST so the subsequent pipe escape's introduced
        // backslashes are not themselves re-escaped on a second pass.
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("|", "\\|", StringComparison.Ordinal);
    }

    /// <summary>
    /// Escapes an extension value per the CEF spec: backslash becomes <c>\\</c>, equals
    /// sign becomes <c>\=</c>, embedded newlines become <c>\n</c>. Carriage returns are
    /// stripped (CEF parsers treat <c>\r</c> inconsistently; dropping them is safer than
    /// preserving them).
    /// </summary>
    /// <param name="value">Raw extension value.</param>
    /// <returns>Extension-escaped string suitable for inclusion after <c>key=</c>.</returns>
    private static string EscapeExtension(string value)
    {
        // Backslash must be escaped first for the same reason as EscapeHeader.
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("=", "\\=", StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
    }

    /// <summary>
    /// Appends one <c>key=value </c> pair (trailing space included) to the supplied
    /// <see cref="StringBuilder"/>. The value is extension-escaped en-route; the key is
    /// emitted verbatim because callers control it (it's never user-supplied).
    /// </summary>
    /// <param name="builder">Target builder accumulating the extension block.</param>
    /// <param name="key">CEF extension key (e.g. <c>"rt"</c>, <c>"suser"</c>).</param>
    /// <param name="value">Raw value to escape and append.</param>
    private static void AppendExtension(StringBuilder builder, string key, string value)
    {
        builder.Append(key).Append('=').Append(EscapeExtension(value)).Append(' ');
    }
}
