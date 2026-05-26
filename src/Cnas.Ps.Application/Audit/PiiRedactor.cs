using System.IO;
using System.Text;
using System.Text.Json;

namespace Cnas.Ps.Application.Audit;

/// <summary>
/// Removes PII values from a JSON details payload before it is persisted to
/// the audit log or mirrored to MLog. SEC 044 / CLAUDE.md §5.6.
/// </summary>
/// <remarks>
/// <para>
/// The match is by KEY name (case-insensitive substring). The matched value is
/// REPLACED with the string <c>"[redacted]"</c> — the key is preserved so
/// audit shape stays stable for downstream parsing.
/// </para>
/// <para>
/// The pattern list is intentionally permissive — it's safer to over-redact
/// than under-redact. If a benign key name happens to match (e.g. a column
/// called <c>"phoneVersionId"</c>), audit it via a different key.
/// </para>
/// <para>
/// Defense in depth: malformed JSON input returns <c>{"_invalidJson":"[redacted]"}</c>
/// so unparseable input cannot leak verbatim into the audit row.
/// </para>
/// </remarks>
public static class PiiRedactor
{
    /// <summary>
    /// Case-insensitive substrings that, when present in a JSON object KEY, cause the
    /// associated value to be replaced by <see cref="RedactedPlaceholder"/>. The list is
    /// shared by the local audit-log writer and any future PII-aware projection.
    /// </summary>
    private static readonly string[] PiiKeySubstrings =
    {
        "idnp", "idno", "cnp",
        "password", "passwd", "pwd",
        "secret", "token", "apikey", "api_key",
        "email", "phone", "msisdn", "mobile",
        "pin",
        "signingkey", "signing_key",
        "iban", "bankaccount", "bank_account", "accountnumber", "account_number",
    };

    /// <summary>Literal text written in place of any value whose key matches a PII substring.</summary>
    private const string RedactedPlaceholder = "[redacted]";

    /// <summary>
    /// Redacts PII values from the supplied JSON document, returning a re-serialised JSON
    /// string with every PII-keyed value replaced by <c>"[redacted]"</c>.
    /// </summary>
    /// <param name="detailsJson">
    /// Caller-supplied JSON payload. May be <c>null</c>, whitespace, malformed, or any
    /// well-formed JSON value (object, array, primitive). The method is total — every
    /// possible input maps to a non-null string return value.
    /// </param>
    /// <returns>
    /// <list type="bullet">
    /// <item><c>"{}"</c> when <paramref name="detailsJson"/> is null / whitespace.</item>
    /// <item><c>"{\"_invalidJson\":\"[redacted]\"}"</c> when <paramref name="detailsJson"/> cannot be parsed as JSON.</item>
    /// <item>A re-serialised JSON string with every PII-keyed value replaced by <c>"[redacted]"</c> otherwise.</item>
    /// </list>
    /// </returns>
    /// <example>
    /// <code>
    /// PiiRedactor.Redact("{\"idnp\":\"2000123456782\",\"status\":\"ok\"}")
    /// // → "{\"idnp\":\"[redacted]\",\"status\":\"ok\"}"
    /// </code>
    /// </example>
    public static string Redact(string? detailsJson) => Redact(detailsJson, extraKeys: null);

    /// <summary>
    /// R0182 / SEC 042 — redaction variant that merges <paramref name="extraKeys"/>
    /// (case-insensitive substring matches) on top of the default PII key list. Used
    /// by the audit drainer when the resolved <see cref="Cnas.Ps.Core.Domain.AuditPolicy"/>
    /// supplies <c>ExtraRedactKeys</c>. Empty / null <paramref name="extraKeys"/>
    /// behaves identically to <see cref="Redact(string?)"/>.
    /// </summary>
    /// <param name="detailsJson">Caller-supplied JSON payload (same contract as the single-arg overload).</param>
    /// <param name="extraKeys">
    /// Additional key substrings to redact, supplied by the matched audit policy. May
    /// be <c>null</c> or empty (no extra redaction).
    /// </param>
    /// <returns>Re-serialised JSON with every default-or-extra-keyed value replaced.</returns>
    public static string Redact(string? detailsJson, IReadOnlySet<string>? extraKeys)
    {
        if (string.IsNullOrWhiteSpace(detailsJson))
        {
            return "{}";
        }

        try
        {
            using var document = JsonDocument.Parse(detailsJson);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                WriteRedacted(document.RootElement, writer, extraKeys);
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (JsonException)
        {
            // Defense in depth — never echo unparseable input that may itself carry PII.
            return "{\"_invalidJson\":\"[redacted]\"}";
        }
    }

    /// <summary>
    /// Recursively writes <paramref name="element"/> to <paramref name="writer"/>,
    /// substituting any object property whose name matches a PII substring (default
    /// or extra) with the redacted placeholder.
    /// </summary>
    /// <param name="element">JSON element being copied.</param>
    /// <param name="writer">Destination writer; receives the redacted projection.</param>
    /// <param name="extraKeys">Optional caller-supplied extra key substrings.</param>
    private static void WriteRedacted(JsonElement element, Utf8JsonWriter writer, IReadOnlySet<string>? extraKeys)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var prop in element.EnumerateObject())
                {
                    writer.WritePropertyName(prop.Name);
                    if (KeyIsPii(prop.Name, extraKeys))
                    {
                        writer.WriteStringValue(RedactedPlaceholder);
                    }
                    else
                    {
                        WriteRedacted(prop.Value, writer, extraKeys);
                    }
                }
                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteRedacted(item, writer, extraKeys);
                }
                writer.WriteEndArray();
                break;

            default:
                // Primitive (string / number / boolean / null) — copy verbatim. Redaction
                // is keyed off the property NAME, not the value, so primitives at the root
                // or inside an array of safe-keyed objects pass through unchanged.
                element.WriteTo(writer);
                break;
        }
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="key"/> matches any of the
    /// case-insensitive PII substrings — default list AND optionally extra keys.
    /// </summary>
    /// <param name="key">Property name being evaluated.</param>
    /// <param name="extraKeys">Optional caller-supplied extra substrings.</param>
    private static bool KeyIsPii(string key, IReadOnlySet<string>? extraKeys)
    {
        foreach (var sub in PiiKeySubstrings)
        {
            if (key.Contains(sub, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        if (extraKeys is not null)
        {
            foreach (var sub in extraKeys)
            {
                if (!string.IsNullOrWhiteSpace(sub)
                    && key.Contains(sub, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        return false;
    }
}
