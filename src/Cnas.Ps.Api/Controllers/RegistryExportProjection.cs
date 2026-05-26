using System.Collections.Generic;
using System.Globalization;
using Cnas.Ps.Contracts;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R0610 / TOR CF 12.01 — iter 125 — tiny helper that projects a page of
/// registry list items into the universal <see cref="ReportExportInputDto"/>
/// shape the <c>IReportExportSelector</c> consumes. Used by the
/// <see cref="ContributorsController"/> and <see cref="InsuredPersonsController"/>
/// SearchAsync endpoints when the caller requests <c>format=xlsx|pdf</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a helper, not per-controller projection.</b> Both registry
/// controllers need the same shape — a (headers + rows) matrix the universal
/// exporter pipeline can consume — and they both produce
/// <see cref="ContributorListItem"/>-style projections. Centralising the
/// translation here keeps the controllers slim and ensures the two registries
/// stay byte-identical on the wire (same header ordering, same
/// invariant-culture boolean rendering, etc.).
/// </para>
/// <para>
/// <b>Confidentiality.</b> The projection captures already-authorised data
/// (the caller passed the role gate on the underlying SearchAsync action);
/// the resulting <see cref="ReportExportInputDto"/> is therefore classified
/// Confidential at class-level by its declaration in Contracts and the
/// <c>SensitivityHeaderMiddleware</c> ensures the matching header lands on
/// the HTTP response.
/// </para>
/// <para>
/// <b>Allocation.</b> The helper materialises arrays of the exact size needed
/// — no <c>ToList</c> followed by <c>ToArray</c> shuffle. The caller is
/// expected to have already capped the page size at the underlying service's
/// 200-row maximum.
/// </para>
/// </remarks>
internal static class RegistryExportProjection
{
    /// <summary>
    /// R0610 — projects a <see cref="ContributorListItem"/> page into a
    /// <see cref="ReportExportInputDto"/> with the headers expected by the
    /// CF 12.01 register-browser export.
    /// </summary>
    /// <param name="title">Display title rendered on the first sheet (XLSX) / first heading (PDF).</param>
    /// <param name="items">Already-paged list of contributor list items.</param>
    /// <returns>A populated <see cref="ReportExportInputDto"/> ready for the universal exporter pipeline.</returns>
    public static ReportExportInputDto ForContributors(
        string title,
        IReadOnlyList<ContributorListItem> items)
    {
        // Headers are fixed and ordered to mirror the JSON SearchAsync payload
        // so the exported file matches what the user sees in the UI grid.
        IReadOnlyList<ReportExportColumnDto> columns =
        [
            new ReportExportColumnDto("Id"),
            new ReportExportColumnDto("Idno"),
            new ReportExportColumnDto("Denumire"),
            new ReportExportColumnDto("IsInsolvent"),
        ];

        var rows = new List<IReadOnlyList<string>>(items.Count);
        foreach (var item in items)
        {
            rows.Add(new[]
            {
                item.Id,
                item.Idno,
                item.Denumire,
                // Invariant-culture rendering so the boolean is stable across exports.
                item.IsInsolvent.ToString(CultureInfo.InvariantCulture),
            });
        }

        return new ReportExportInputDto(title, columns, rows);
    }

    /// <summary>
    /// R0610 — projects an <see cref="InsuredPersonListItem"/> page into a
    /// <see cref="ReportExportInputDto"/> with the headers expected by the
    /// CF 12.01 register-browser export.
    /// </summary>
    /// <param name="title">Display title rendered on the first sheet (XLSX) / first heading (PDF).</param>
    /// <param name="items">Already-paged list of insured-person list items.</param>
    /// <returns>A populated <see cref="ReportExportInputDto"/> ready for the universal exporter pipeline.</returns>
    public static ReportExportInputDto ForInsuredPersons(
        string title,
        IReadOnlyList<InsuredPersonListItem> items)
    {
        IReadOnlyList<ReportExportColumnDto> columns =
        [
            new ReportExportColumnDto("Id"),
            new ReportExportColumnDto("Idnp"),
            new ReportExportColumnDto("FullName"),
            new ReportExportColumnDto("IsDeceased"),
        ];

        var rows = new List<IReadOnlyList<string>>(items.Count);
        foreach (var item in items)
        {
            rows.Add(new[]
            {
                item.Id,
                item.Idnp,
                item.FullName,
                item.IsDeceased.ToString(CultureInfo.InvariantCulture),
            });
        }

        return new ReportExportInputDto(title, columns, rows);
    }
}
