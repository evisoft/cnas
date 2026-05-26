using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.PublicServices;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.Services.PublicServices;

/// <summary>
/// R0512 — strongly-typed options for the anonymous online-appointment
/// service. Binds from <c>Cnas:PublicServices:Appointments</c> in
/// configuration.
/// </summary>
public sealed class AppointmentBookingOptions
{
    /// <summary>Configuration section under which these options are bound.</summary>
    public const string SectionName = "Cnas:PublicServices:Appointments";

    /// <summary>
    /// System-wide deep-link URL template with a single <c>{branchCode}</c>
    /// placeholder. Default points at the public <c>programare.cnas.md</c>
    /// scheduler.
    /// </summary>
    public string DeepLinkTemplate { get; set; } = "https://programare.cnas.md/?branch={branchCode}&lang=ro";
}

/// <summary>
/// R0512 / TOR CF 02.01 — implementation of
/// <see cref="IOnlineAppointmentBookingService"/>. Surfaces the active
/// <see cref="CnasBranch"/> rows from the database, alphabetised by name, and
/// resolves per-branch deep-link URLs against the configured template.
/// </summary>
public sealed class OnlineAppointmentBookingService : IOnlineAppointmentBookingService
{
    /// <summary>Audit event code emitted on every successful resolve call.</summary>
    public const string AuditEventCode = "PUBLIC.APPOINTMENT_DEEPLINK";

    /// <summary>Placeholder substituted into the deep-link template.</summary>
    public const string BranchCodePlaceholder = "{branchCode}";

    private readonly ICnasDbContext _db;
    private readonly IAuditService _audit;
    private readonly ICallerContext _caller;
    private readonly IOptions<AppointmentBookingOptions> _options;

    /// <summary>Constructs the service with its collaborators.</summary>
    /// <param name="db">EF Core context for the <c>CnasBranches</c> table.</param>
    /// <param name="audit">Audit-log façade — resolve calls write one Information row.</param>
    /// <param name="caller">Per-request caller context — used for source-IP + correlation on the audit row.</param>
    /// <param name="options">Bound <see cref="AppointmentBookingOptions"/>.</param>
    public OnlineAppointmentBookingService(
        ICnasDbContext db,
        IAuditService audit,
        ICallerContext caller,
        IOptions<AppointmentBookingOptions> options)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(options);
        _db = db;
        _audit = audit;
        _caller = caller;
        _options = options;
    }

    /// <inheritdoc />
    public async Task<Result<AppointmentBookingDirectoryDto>> GetDirectoryAsync(CancellationToken ct = default)
    {
        // Active branches only — the soft-delete filter hides operator-disabled
        // rows from the public surface. Stable ordering by Name keeps the JSON
        // response cacheable.
        var rows = await _db.CnasBranches
            .Where(b => b.IsActive)
            .OrderBy(b => b.Name)
            .Select(b => new AppointmentBranchDto(
                b.Code,
                b.Name,
                b.City,
                b.Address,
                b.Phone))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return Result<AppointmentBookingDirectoryDto>.Success(new AppointmentBookingDirectoryDto(
            Branches: rows,
            DeepLinkTemplate: _options.Value.DeepLinkTemplate));
    }

    /// <inheritdoc />
    public async Task<Result<AppointmentDeepLinkDto>> ResolveDeepLinkAsync(
        string branchCode,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(branchCode))
        {
            return Result<AppointmentDeepLinkDto>.Failure(
                ErrorCodes.NotFound,
                "BRANCH_NOT_FOUND");
        }

        // Look up the branch — must be active. Per-branch URL template
        // overrides the global default when populated.
        var branch = await _db.CnasBranches
            .Where(b => b.Code == branchCode && b.IsActive)
            .Select(b => new { b.Code, b.OnlineSchedulingUrlTemplate })
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (branch is null)
        {
            return Result<AppointmentDeepLinkDto>.Failure(
                ErrorCodes.NotFound,
                "BRANCH_NOT_FOUND");
        }

        var template = branch.OnlineSchedulingUrlTemplate ?? _options.Value.DeepLinkTemplate;
        var url = template.Replace(BranchCodePlaceholder, branch.Code, StringComparison.Ordinal);

        // Write the audit row AFTER the URL is rendered so an audit failure
        // doesn't double-charge a successful resolve. Information severity —
        // resolving a deep-link is a read with side-effect-free downstream.
        var details = JsonSerializer.Serialize(new { branch = branch.Code });
        await _audit.RecordAsync(
            eventCode: AuditEventCode,
            severity: AuditSeverity.Information,
            actorId: _caller.UserSqid ?? "anonymous",
            targetEntity: nameof(CnasBranch),
            targetEntityId: null,
            detailsJson: details,
            sourceIp: _caller.SourceIp,
            correlationId: _caller.CorrelationId,
            cancellationToken: ct).ConfigureAwait(false);

        return Result<AppointmentDeepLinkDto>.Success(new AppointmentDeepLinkDto(url));
    }
}
