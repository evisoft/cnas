using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Dashboard;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.Dashboard;

/// <summary>
/// R0533 / TOR CF 04.04 — KPI grid cell counting <see cref="ServiceApplication"/> rows
/// in the <see cref="ApplicationStatus.Submitted"/> state. Provides the at-a-glance
/// "applications in intake" KPI on the operator dashboard, with a deep-link to the
/// pre-filtered applications-list page so a click drills into the underlying records.
/// </summary>
/// <remarks>
/// <para>
/// <b>Role gating.</b> Wildcard — every authenticated caller sees the cell. Citizens
/// see only their own submissions (the underlying list page applies the per-user
/// filter); staff see the global queue.
/// </para>
/// </remarks>
public sealed class ApplicationsByStatusKpiGridProducer(
    IReadOnlyCnasDbContext db) : IKpiGridProducer
{
    /// <summary>Stable KPI cell code.</summary>
    public const string CellCode = "APPLICATIONS_SUBMITTED";

    /// <summary>Pre-allocated wildcard role list.</summary>
    private static readonly string[] AnyRole = ["*"];

    private readonly IReadOnlyCnasDbContext _db = db;

    /// <inheritdoc />
    public IReadOnlyCollection<string> SupportedRoles => AnyRole;

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<KpiGridCellDto>>> ProduceAsync(
        long userId,
        CancellationToken cancellationToken = default)
    {
        _ = userId; // Reserved for the per-caller filter on the citizen variant.

        var count = await _db.Applications
            .Where(a => a.IsActive && a.Status == ApplicationStatus.Submitted)
            .LongCountAsync(cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<KpiGridCellDto> cells =
        [
            new KpiGridCellDto(
                Code: CellCode,
                Title: "Cereri depuse",
                Value: count,
                Trend: null,
                DeepLinkUrl: "/applications?status=Submitted"),
        ];
        return Result<IReadOnlyList<KpiGridCellDto>>.Success(cells);
    }
}
