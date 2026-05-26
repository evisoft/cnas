using System.Reflection;
using System.Text;
using System.Xml.Linq;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// R2164 / TOR §15.4 INT 005 — WSDL portal façade. Reflects over a supplied controller
/// assembly and emits a minimal WSDL 1.1 document per controller, sufficient for legacy
/// SOAP clients to bind a compatibility layer over the REST API surface.
/// </summary>
/// <remarks>
/// <para>
/// The service is intentionally schema-light: SOAP-stub tooling only needs the operation
/// names + a valid (parseable) WSDL skeleton in order to generate proxy classes. Per-
/// action body shapes remain governed by the REST/OpenAPI contract that downstream
/// clients should additionally consume.
/// </para>
/// <para>
/// <b>Reflection scope.</b> The constructor accepts the <see cref="Assembly"/> to scan
/// (defaults to <see cref="MethodBase.GetCurrentMethod"/> → assembly of the calling
/// code at composition time). In production this is wired to the Cnas.Ps.Api assembly
/// via DI so the portal lists every REST controller. Tests can pass their own assembly
/// (e.g. via <c>typeof(SomeController).Assembly</c>) to exercise the contract without
/// booting the full API host.
/// </para>
/// <para>
/// <b>Why Infrastructure.</b> The service has no controller-specific knowledge — it only
/// needs <see cref="System.Reflection"/> + the conventional <c>Controller</c> naming
/// suffix to filter. Placing it in Infrastructure keeps Cnas.Ps.Api thin and lets future
/// ops surfaces (Helmsman, BBT) consume the same façade without taking a hard dependency
/// on the API project (and without dragging <c>Microsoft.AspNetCore.Mvc</c> into
/// Infrastructure, which the architecture suite forbids).
/// </para>
/// </remarks>
/// <param name="controllerAssembly">Assembly whose <c>*Controller</c>-named types are surfaced through the portal.</param>
public sealed class WsdlPortalService(Assembly controllerAssembly) : IWsdlPortalService
{
    private readonly Assembly _controllerAssembly = controllerAssembly;

    /// <summary>
    /// XML namespace used for the WSDL 1.1 envelope. Hard-coded constant rather than a
    /// magic literal scattered across the writer — see CLAUDE.md "config as constants".
    /// </summary>
    private const string WsdlNs = "http://schemas.xmlsoap.org/wsdl/";

    /// <summary>
    /// XML-schema namespace for the placeholder string body parameter on every
    /// operation. Real schema fidelity is out of scope for the portal; SOAP-stub tooling
    /// only needs a parseable signature.
    /// </summary>
    private const string XsdNs = "http://www.w3.org/2001/XMLSchema";

    /// <summary>
    /// Target namespace embedded in generated WSDL documents. Stable so SOAP proxy
    /// regenerations don't rename the generated client classes underneath consumers.
    /// </summary>
    private const string TargetNs = "https://cnas.gov.md/ps/wsdl-portal";

    /// <inheritdoc />
    public Task<Result<IReadOnlyList<WsdlListingDto>>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var rows = DiscoverControllers()
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .Select(t =>
            {
                var bareName = StripControllerSuffix(t.Name);
                var operations = DiscoverActions(t).Count;
                return new WsdlListingDto(
                    ControllerName: bareName,
                    WsdlUrl: $"/api/wsdl-portal/{bareName}.wsdl",
                    OperationCount: operations);
            })
            .ToList();

        IReadOnlyList<WsdlListingDto> ro = rows;
        return Task.FromResult(Result<IReadOnlyList<WsdlListingDto>>.Success(ro));
    }

    /// <inheritdoc />
    public Task<Result<WsdlDescriptorDto>> GetForControllerAsync(
        string controllerName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(controllerName))
        {
            return Task.FromResult(Result<WsdlDescriptorDto>.Failure(
                ErrorCodes.ValidationFailed,
                "Controller name is required."));
        }

        var bare = StripControllerSuffix(controllerName.Trim());

        var match = DiscoverControllers()
            .FirstOrDefault(t => StripControllerSuffix(t.Name)
                .Equals(bare, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            return Task.FromResult(Result<WsdlDescriptorDto>.Failure(
                ErrorCodes.NotFound,
                $"No controller named '{bare}' was found in the WSDL portal."));
        }

        var canonicalName = StripControllerSuffix(match.Name);
        var actions = DiscoverActions(match);
        var xml = BuildWsdl(canonicalName, actions);

        return Task.FromResult(Result<WsdlDescriptorDto>.Success(new WsdlDescriptorDto(
            ControllerName: canonicalName,
            WsdlXml: xml,
            Operations: actions)));
    }

    /// <summary>
    /// Returns every concrete, public, non-abstract type from the scanned assembly whose
    /// name ends with the conventional <c>Controller</c> suffix. The check is name-based
    /// rather than type-based on purpose: <c>WsdlPortalService</c> lives in
    /// Cnas.Ps.Infrastructure, which must not take a hard dependency on
    /// <c>Microsoft.AspNetCore.Mvc</c> (LayerBoundaryTests asserts this). The name-
    /// convention contract is enforced repository-wide by the architecture tests so the
    /// duck-type filter is safe.
    /// </summary>
    /// <returns>Sequence of concrete controller types.</returns>
    private IEnumerable<Type> DiscoverControllers()
    {
        return _controllerAssembly
            .GetExportedTypes()
            .Where(t =>
                t.IsClass
                && !t.IsAbstract
                && !t.IsNested
                && t.Name.EndsWith("Controller", StringComparison.Ordinal));
    }

    /// <summary>
    /// Returns the public action method names of a controller. Filters out compiler-
    /// generated members and any inherited base-class helpers (via <c>DeclaredOnly</c>)
    /// so each surfaced name corresponds to a real action defined directly on the
    /// controller class.
    /// </summary>
    /// <param name="controller">Concrete controller type.</param>
    /// <returns>Distinct action names sorted ordinally.</returns>
    private static IReadOnlyList<string> DiscoverActions(Type controller)
    {
        return controller
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .Select(m => m.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Strips the conventional <c>Controller</c> suffix when present.
    /// </summary>
    /// <param name="typeName">Raw type name (e.g. <c>HealthController</c>).</param>
    /// <returns>Suffix-stripped name (e.g. <c>Health</c>).</returns>
    private static string StripControllerSuffix(string typeName) =>
        typeName.EndsWith("Controller", StringComparison.Ordinal)
            ? typeName[..^"Controller".Length]
            : typeName;

    /// <summary>
    /// Constructs a minimal WSDL 1.1 document for <paramref name="controllerName"/>
    /// exposing each entry in <paramref name="operations"/> as a SOAP operation that
    /// accepts and returns a single string body. The output is guaranteed to be a
    /// well-formed XML document (parseable via <see cref="XDocument.Parse(string)"/>).
    /// </summary>
    /// <param name="controllerName">Suffix-stripped controller name (becomes the WSDL service name).</param>
    /// <param name="operations">Action method names to expose as SOAP operations.</param>
    /// <returns>The generated WSDL XML body.</returns>
    private static string BuildWsdl(string controllerName, IReadOnlyList<string> operations)
    {
        var wsdl = XNamespace.Get(WsdlNs);
        var xsd = XNamespace.Get(XsdNs);
        var tns = XNamespace.Get(TargetNs);

        var types = new XElement(wsdl + "types",
            new XElement(xsd + "schema",
                new XAttribute("targetNamespace", TargetNs),
                new XAttribute(XNamespace.Xmlns + "xsd", XsdNs),
                new XElement(xsd + "element",
                    new XAttribute("name", "StringPayload"),
                    new XAttribute("type", "xsd:string"))));

        var messages = new List<XElement>();
        var portTypeOps = new List<XElement>();
        var bindingOps = new List<XElement>();

        foreach (var op in operations)
        {
            var requestMsg = $"{op}Request";
            var responseMsg = $"{op}Response";

            messages.Add(new XElement(wsdl + "message",
                new XAttribute("name", requestMsg),
                new XElement(wsdl + "part",
                    new XAttribute("name", "body"),
                    new XAttribute("element", "tns:StringPayload"))));

            messages.Add(new XElement(wsdl + "message",
                new XAttribute("name", responseMsg),
                new XElement(wsdl + "part",
                    new XAttribute("name", "body"),
                    new XAttribute("element", "tns:StringPayload"))));

            portTypeOps.Add(new XElement(wsdl + "operation",
                new XAttribute("name", op),
                new XElement(wsdl + "input",
                    new XAttribute("message", $"tns:{requestMsg}")),
                new XElement(wsdl + "output",
                    new XAttribute("message", $"tns:{responseMsg}"))));

            bindingOps.Add(new XElement(wsdl + "operation",
                new XAttribute("name", op),
                new XElement(wsdl + "input",
                    new XAttribute("name", requestMsg)),
                new XElement(wsdl + "output",
                    new XAttribute("name", responseMsg))));
        }

        var portType = new XElement(wsdl + "portType",
            new XAttribute("name", $"{controllerName}PortType"),
            portTypeOps);

        var binding = new XElement(wsdl + "binding",
            new XAttribute("name", $"{controllerName}Binding"),
            new XAttribute("type", $"tns:{controllerName}PortType"),
            bindingOps);

        var service = new XElement(wsdl + "service",
            new XAttribute("name", $"{controllerName}Service"),
            new XElement(wsdl + "port",
                new XAttribute("name", $"{controllerName}Port"),
                new XAttribute("binding", $"tns:{controllerName}Binding")));

        var definitions = new XElement(wsdl + "definitions",
            new XAttribute("name", controllerName),
            new XAttribute("targetNamespace", TargetNs),
            new XAttribute(XNamespace.Xmlns + "tns", TargetNs),
            new XAttribute(XNamespace.Xmlns + "xsd", XsdNs),
            types,
            messages,
            portType,
            binding,
            service);

        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.Append(definitions);
        return sb.ToString();
    }
}
