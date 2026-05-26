using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.UseCases;

/// <summary>
/// R2135 / TOR §15.2 ARH 026 — XSD export façade for the public Contracts DTOs.
/// Closes the "documented data model with XSD" half of ARH 026 by generating an
/// XML Schema (XSD) artefact on demand for any whitelisted DTO type so external
/// integrators (and the data-model glossary) have a machine-readable contract
/// to consume.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope.</b> The service is intentionally narrow: a handful of representative
/// DTOs are wired into a starter allow-list (see implementation). The output is
/// pure XSD 1.0 produced by a reflection-based emitter over the DTO record's
/// public properties; SOAP-stub tooling, schema registries, and the
/// documentation portal all consume the same artefact verbatim.
/// </para>
/// <para>
/// <b>Why an allow-list.</b> Reflecting over arbitrary type names from inbound
/// HTTP traffic would be an open invitation for type-enumeration probing.
/// Pinning the surface to a small, curated map keeps the endpoint operationally
/// boring and aligns with CLAUDE.md "deny by default" (Phase 5.4). New DTOs are
/// added by extending the implementation's static map in code review.
/// </para>
/// <para>
/// <b>Sqid contract.</b> Most DTOs carry their external ids as <see cref="string"/>
/// columns (CLAUDE.md RULE 3). The generated XSD therefore types every <c>Id</c>
/// + <c>*Id</c> field as <c>xs:string</c> — exactly the wire shape an integrator
/// receives. There is no leakage of the raw 64-bit primary key.
/// </para>
/// </remarks>
public interface IXsdExportService
{
    /// <summary>
    /// Returns the XSD 1.0 schema document for the DTO identified by
    /// <paramref name="dtoTypeName"/>.
    /// </summary>
    /// <param name="dtoTypeName">
    /// Bare type name of the DTO (case-insensitive). Examples: <c>ApplicationOutput</c>,
    /// <c>ClaimDto</c>, <c>NotificationOutput</c>. The full <c>Cnas.Ps.Contracts.*</c>
    /// namespace prefix is NOT required; the service resolves the type from the
    /// curated allow-list.
    /// </param>
    /// <param name="cancellationToken">Co-operative cancellation.</param>
    /// <returns>
    /// <see cref="Result{T}.Success(T)"/> with the generated XSD body as a UTF-8 string
    /// on success; <see cref="ErrorCodes.NotFound"/> when the DTO name is not in the
    /// allow-list; <see cref="ErrorCodes.ValidationFailed"/> when the input is empty.
    /// </returns>
    Task<Result<string>> ExportAsync(
        string dtoTypeName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the bare type names of every DTO available through the portal, sorted
    /// alphabetically. Backs the admin landing page that lists every downloadable
    /// schema.
    /// </summary>
    /// <param name="cancellationToken">Co-operative cancellation.</param>
    /// <returns>Listing of supported DTO names; never empty in a healthy build.</returns>
    Task<Result<IReadOnlyList<string>>> ListAsync(
        CancellationToken cancellationToken = default);
}
