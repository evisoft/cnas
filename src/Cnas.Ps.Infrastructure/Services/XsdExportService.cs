using System.Collections;
using System.Reflection;
using System.Text;
using System.Xml.Linq;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// R2135 / TOR §15.2 ARH 026 — XSD export façade for the public Contracts DTOs.
/// Generates an XML Schema (XSD) document on demand for any DTO in the curated
/// allow-list, closing the "documented data model with XSD" half of ARH 026.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reflection-based emitter.</b> The implementation does NOT use the
/// legacy <c>XmlSerializer</c> / <c>XmlSchemaExporter</c> pipeline because the
/// underlying DTOs freely use <see cref="DateOnly"/>, <see cref="decimal"/>,
/// nullable value types, and primary-constructor records — which the legacy
/// serializer support is brittle against. Instead the service inspects the
/// DTO type's
/// public read-only properties (the primary-constructor parameters of a
/// record) and emits XSD elements by mapping each .NET type to its XSD
/// counterpart through a deterministic <see cref="MapTypeToXsd"/> table.
/// </para>
/// <para>
/// <b>Allow-list policy.</b> Only the DTOs registered in <see cref="AllowList"/>
/// can be exported. New entries are added by code review; arbitrary type-name
/// reflection from inbound HTTP traffic is rejected by design (CLAUDE.md §5.4
/// "deny by default").
/// </para>
/// <para>
/// <b>Sqid contract.</b> Every <c>Id</c> / <c>*Id</c> property is a
/// <see cref="string"/> on the DTO (CLAUDE.md RULE 3); the type-mapping table
/// surfaces it as <c>xs:string</c>. The internal 64-bit primary key never
/// reaches an XSD artefact.
/// </para>
/// </remarks>
public sealed class XsdExportService : IXsdExportService
{
    /// <summary>XML Schema namespace constant.</summary>
    private const string XsdNamespace = "http://www.w3.org/2001/XMLSchema";

    /// <summary>
    /// Stable target namespace embedded in every generated XSD so external
    /// catalog tooling can reliably group SI-PS schemas under a single URI.
    /// </summary>
    private const string TargetNamespace = "https://cnas.gov.md/ps/xsd";

    /// <summary>
    /// Curated allow-list of DTO types whose XSD may be exported. The map keys
    /// are the bare (case-sensitive) type names; the values are the canonical
    /// <see cref="Type"/> handles so the emitter can reflect over them.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, Type> AllowList =
        new Dictionary<string, Type>(StringComparer.Ordinal)
        {
            // R2135 starter set — five representative DTOs covering the public
            // application surface (UC06 / UC03), the notifications inbox
            // (UC11), the claims registry (BP 1.3-B), the decisions explorer
            // (UC09), and the documents explorer (UC10).
            [nameof(ApplicationOutput)] = typeof(ApplicationOutput),
            [nameof(NotificationOutput)] = typeof(NotificationOutput),
            [nameof(ClaimDto)] = typeof(ClaimDto),
            [nameof(DecisionListItemDto)] = typeof(DecisionListItemDto),
            [nameof(DocumentListItemDto)] = typeof(DocumentListItemDto),
        };

    /// <inheritdoc />
    public Task<Result<IReadOnlyList<string>>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var rows = AllowList.Keys
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        IReadOnlyList<string> ro = rows;
        return Task.FromResult(Result<IReadOnlyList<string>>.Success(ro));
    }

    /// <inheritdoc />
    public Task<Result<string>> ExportAsync(
        string dtoTypeName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(dtoTypeName))
        {
            return Task.FromResult(Result<string>.Failure(
                ErrorCodes.ValidationFailed,
                "DTO type name is required."));
        }

        var trimmed = dtoTypeName.Trim();

        // Case-insensitive lookup against the case-sensitive allow-list map.
        var match = AllowList
            .FirstOrDefault(kv => string.Equals(kv.Key, trimmed, StringComparison.OrdinalIgnoreCase));
        if (match.Key is null)
        {
            return Task.FromResult(Result<string>.Failure(
                ErrorCodes.NotFound,
                $"No DTO named '{trimmed}' is registered for XSD export."));
        }

        var xsd = BuildXsd(match.Key, match.Value);
        return Task.FromResult(Result<string>.Success(xsd));
    }

    /// <summary>
    /// Builds the XSD body for <paramref name="dtoName"/> by reflecting over
    /// the public read-only properties of <paramref name="dtoType"/> and
    /// translating each into an <c>&lt;xs:element&gt;</c> entry inside a named
    /// complex type.
    /// </summary>
    /// <param name="dtoName">Canonical DTO type name (e.g. <c>ApplicationOutput</c>).</param>
    /// <param name="dtoType">Reflected type handle of the DTO.</param>
    /// <returns>Well-formed XSD 1.0 document as a UTF-8 string.</returns>
    private static string BuildXsd(string dtoName, Type dtoType)
    {
        XNamespace xs = XsdNamespace;
        XNamespace tns = TargetNamespace;

        var sequence = new XElement(xs + "sequence");
        foreach (var prop in dtoType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0)
            .OrderBy(p => p.MetadataToken)) // declaration order on records
        {
            var (xsdType, isCollection) = MapTypeToXsd(prop.PropertyType);
            var nillable = IsNullable(prop);

            var element = new XElement(xs + "element",
                new XAttribute("name", prop.Name),
                new XAttribute("type", xsdType));
            if (isCollection)
            {
                element.SetAttributeValue("minOccurs", "0");
                element.SetAttributeValue("maxOccurs", "unbounded");
            }
            else if (nillable)
            {
                element.SetAttributeValue("minOccurs", "0");
                element.SetAttributeValue("nillable", "true");
            }
            sequence.Add(element);
        }

        var complexType = new XElement(xs + "complexType",
            new XAttribute("name", $"{dtoName}Type"),
            sequence);

        var topLevel = new XElement(xs + "element",
            new XAttribute("name", dtoName),
            new XAttribute("type", $"tns:{dtoName}Type"));

        var schema = new XElement(xs + "schema",
            new XAttribute("targetNamespace", TargetNamespace),
            new XAttribute("elementFormDefault", "qualified"),
            new XAttribute(XNamespace.Xmlns + "xs", XsdNamespace),
            new XAttribute(XNamespace.Xmlns + "tns", TargetNamespace),
            topLevel,
            complexType);

        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.Append(schema);
        return sb.ToString();
    }

    /// <summary>
    /// Maps a .NET property type onto its XSD counterpart. Unknown / opaque
    /// reference types fall through to <c>xs:string</c> — the safest wire
    /// representation for tooling that cannot bind a richer shape.
    /// </summary>
    /// <param name="t">Property type from the DTO record.</param>
    /// <returns>
    /// (xsd-type-name-with-xs-prefix, isCollection) tuple. The collection
    /// flag triggers <c>minOccurs="0" maxOccurs="unbounded"</c> on the
    /// surrounding element.
    /// </returns>
    private static (string Type, bool IsCollection) MapTypeToXsd(Type t)
    {
        // Strip Nullable<T> wrapper.
        var underlying = Nullable.GetUnderlyingType(t) ?? t;

        // Detect collection element type (string / arrays / IEnumerable<T>).
        // string implements IEnumerable<char> so it must be excluded first.
        if (underlying != typeof(string))
        {
            if (underlying.IsArray)
            {
                var elem = underlying.GetElementType()!;
                return (MapScalar(elem), true);
            }

            if (typeof(IEnumerable).IsAssignableFrom(underlying))
            {
                var generic = underlying.IsGenericType
                    ? underlying.GetGenericArguments().FirstOrDefault()
                    : null;
                if (generic is not null)
                {
                    return (MapScalar(generic), true);
                }
                return ("xs:string", true);
            }
        }

        return (MapScalar(underlying), false);
    }

    /// <summary>
    /// Maps a single scalar .NET type to its xs-namespace counterpart.
    /// </summary>
    /// <param name="t">Scalar .NET type (after Nullable / collection unwrap).</param>
    /// <returns>XSD type name with <c>xs:</c> prefix.</returns>
    private static string MapScalar(Type t)
    {
        var underlying = Nullable.GetUnderlyingType(t) ?? t;

        if (underlying == typeof(string)) return "xs:string";
        if (underlying == typeof(bool)) return "xs:boolean";
        if (underlying == typeof(byte)) return "xs:unsignedByte";
        if (underlying == typeof(sbyte)) return "xs:byte";
        if (underlying == typeof(short)) return "xs:short";
        if (underlying == typeof(ushort)) return "xs:unsignedShort";
        if (underlying == typeof(int)) return "xs:int";
        if (underlying == typeof(uint)) return "xs:unsignedInt";
        if (underlying == typeof(long)) return "xs:long";
        if (underlying == typeof(ulong)) return "xs:unsignedLong";
        if (underlying == typeof(float)) return "xs:float";
        if (underlying == typeof(double)) return "xs:double";
        if (underlying == typeof(decimal)) return "xs:decimal";
        if (underlying == typeof(DateTime)) return "xs:dateTime";
        if (underlying == typeof(DateTimeOffset)) return "xs:dateTime";
        if (underlying == typeof(DateOnly)) return "xs:date";
        if (underlying == typeof(TimeOnly)) return "xs:time";
        if (underlying == typeof(TimeSpan)) return "xs:duration";
        if (underlying == typeof(Guid)) return "xs:string";
        if (underlying.IsEnum) return "xs:string";
        // Fallback — unknown reference types surface as xs:string. Real schema
        // fidelity belongs to richer artefacts (OpenAPI). XSD here is the
        // minimal portable shape per ARH 026.
        return "xs:string";
    }

    /// <summary>
    /// Returns true when the property's CLR type permits a <c>null</c> value
    /// (either a nullable value type or a reference type without a
    /// <c>required</c> + non-nullable annotation).
    /// </summary>
    /// <param name="prop">Reflected property descriptor.</param>
    /// <returns>True when the element should be marked nillable in the XSD.</returns>
    private static bool IsNullable(PropertyInfo prop)
    {
        if (Nullable.GetUnderlyingType(prop.PropertyType) is not null)
        {
            return true;
        }
        if (prop.PropertyType.IsValueType)
        {
            return false;
        }
        // Reference types are conservatively treated as nullable when the
        // nullability annotation context is disabled or ambiguous. For records
        // with primary-constructor parameters this is a documentation hint
        // only — it does NOT change wire validation.
        return true;
    }
}
