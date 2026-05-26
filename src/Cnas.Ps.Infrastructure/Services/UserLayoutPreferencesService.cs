using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.UserLayout;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Core.ValueObjects;
using Cnas.Ps.Infrastructure.Observability;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// R0535 / CF 04.07-08 — default implementation of
/// <see cref="IUserLayoutPreferencesService"/>. Mirrors the
/// <c>ProfileService.GetNotificationPreferencesAsync / SetNotificationPreferencesAsync</c>
/// design: the JSON column on <see cref="UserProfile"/> is the single source of truth,
/// parsing happens at the application boundary, and the dispatcher's default shape is
/// returned whenever the column is NULL or malformed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifetime.</b> Scoped — captures the per-request <see cref="ICnasDbContext"/>,
/// <see cref="ICallerContext"/>, and <see cref="ICnasTimeProvider"/>. Wired in
/// <c>InfrastructureServiceCollectionExtensions</c> alongside <c>ProfileService</c>.
/// </para>
/// <para>
/// <b>Audit contract.</b> Exactly one audit row per successful save, with stable event
/// code <c>USER.LAYOUT.UPDATED</c>, severity <see cref="AuditSeverity.Information"/>,
/// and a small details payload counting the saved grids + widgets so operators can
/// chart layout-customisation adoption. Read calls are NOT audited — the volume would
/// dominate the audit log without providing operator value.
/// </para>
/// </remarks>
public sealed class UserLayoutPreferencesService : IUserLayoutPreferencesService
{
    /// <summary>Stable audit event code for a successful layout save.</summary>
    public const string AuditEventCode = "USER.LAYOUT.UPDATED";

    private readonly ICnasDbContext _db;
    private readonly ICallerContext _caller;
    private readonly ICnasTimeProvider _clock;
    private readonly IAuditService _audit;
    private readonly ILogger<UserLayoutPreferencesService> _logger;

    /// <summary>Constructs the service with its scoped collaborators.</summary>
    /// <param name="db">Per-request EF Core context.</param>
    /// <param name="caller">Authenticated caller — supplies the actor id for audit rows.</param>
    /// <param name="clock">UTC clock — never <see cref="DateTime.UtcNow"/> directly.</param>
    /// <param name="audit">Audit journal façade.</param>
    /// <param name="logger">Structured logger.</param>
    public UserLayoutPreferencesService(
        ICnasDbContext db,
        ICallerContext caller,
        ICnasTimeProvider clock,
        IAuditService audit,
        ILogger<UserLayoutPreferencesService> logger)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(logger);

        _db = db;
        _caller = caller;
        _clock = clock;
        _audit = audit;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<UserLayoutPreferencesDto> GetForCurrentUserAsync(
        CancellationToken cancellationToken = default)
    {
        if (_caller.UserId is null)
        {
            // Anonymous callers receive the system defaults; the controller's
            // [Authorize] gate makes this branch theoretical, but the service must
            // not throw on the defensive path either.
            return ToDto(UserLayoutPreferences.Default);
        }

        var json = await _db.UserProfiles
            .Where(u => u.Id == _caller.UserId.Value && u.IsActive)
            .Select(u => new { u.Id, u.LayoutPreferences })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (json is null)
        {
            // The user row has been deactivated; fall back to defaults so the UI can
            // still render. The caller will be denied on the next mutating call.
            return ToDto(UserLayoutPreferences.Default);
        }

        var outcome = UserLayoutPreferencesJson.TryParse(json.LayoutPreferences);
        if (!outcome.Succeeded)
        {
            CnasMeter.UserLayoutParseFailure.Add(1);
            _logger.LogWarning(
                "UserLayoutPreferencesService.GetForCurrentUser: malformed JSON for user {UserId}, returning defaults.",
                json.Id);
        }
        return ToDto(outcome.Value);
    }

    /// <inheritdoc />
    public async Task<Result<UserLayoutPreferencesDto>> SaveAsync(
        UserLayoutPreferencesSaveDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (_caller.UserId is null)
        {
            return Result<UserLayoutPreferencesDto>.Failure(
                ErrorCodes.Unauthorized, "Not authenticated.");
        }

        // Defensive validation — the FluentValidation pipeline runs before us when the
        // controller is invoked through the HTTP boundary, but service-level callers
        // (jobs, integration tests) may construct invalid DTOs directly. We mirror the
        // validator's rules so the service contract is self-enforcing.
        var validator = new Cnas.Ps.Application.Validators.UserLayoutPreferencesSaveDtoValidator();
        var validation = validator.Validate(input);
        if (!validation.IsValid)
        {
            var detail = string.Join("; ", validation.Errors.Select(e => e.ErrorMessage));
            return Result<UserLayoutPreferencesDto>.Failure(ErrorCodes.ValidationFailed, detail);
        }

        var user = await _db.UserProfiles
            .SingleOrDefaultAsync(u => u.Id == _caller.UserId.Value && u.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (user is null)
        {
            return Result<UserLayoutPreferencesDto>.Failure(ErrorCodes.NotFound, "Profile not found.");
        }

        var prefs = FromDto(input);
        user.LayoutPreferences = UserLayoutPreferencesJson.Serialize(prefs);
        user.UpdatedAtUtc = _clock.UtcNow;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Audit Information — operators can chart layout-customisation adoption without
        // the row volume dominating the audit log.
        var detailsJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            gridCount = prefs.Grids.Count,
            widgetOrderCount = prefs.DashboardWidgetOrder.Count,
            defaultPageSize = prefs.DefaultPageSize,
        });
        await _audit.RecordAsync(
            eventCode: AuditEventCode,
            severity: AuditSeverity.Information,
            actorId: _caller.UserSqid ?? $"user:{_caller.UserId.Value}",
            targetEntity: nameof(UserProfile),
            targetEntityId: user.Id,
            detailsJson: detailsJson,
            sourceIp: _caller.SourceIp,
            correlationId: _caller.CorrelationId,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return Result<UserLayoutPreferencesDto>.Success(ToDto(prefs));
    }

    /// <summary>
    /// Converts the persisted value object into the wire-shaped DTO. The dictionary
    /// returned is a fresh case-insensitive copy so callers can mutate it without
    /// leaking back into the cached preference state.
    /// </summary>
    /// <param name="prefs">Source value object.</param>
    /// <returns>Wire-shaped DTO ready for serialisation.</returns>
    private static UserLayoutPreferencesDto ToDto(UserLayoutPreferences prefs)
    {
        var grids = new Dictionary<string, GridLayoutDto>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in prefs.Grids)
        {
            grids[kv.Key] = new GridLayoutDto(
                VisibleColumns: kv.Value.VisibleColumns.ToList(),
                ColumnOrder: kv.Value.ColumnOrder.ToList(),
                PageSize: kv.Value.PageSize);
        }
        return new UserLayoutPreferencesDto(
            Grids: grids,
            DefaultPageSize: prefs.DefaultPageSize,
            DashboardWidgetOrder: prefs.DashboardWidgetOrder.ToList());
    }

    /// <summary>
    /// Converts an inbound write DTO into the persistable value object. Null
    /// dictionaries / lists become empty so the JSON column never carries a
    /// <c>null</c> sub-document.
    /// </summary>
    /// <param name="input">Inbound write DTO.</param>
    /// <returns>Value object ready for serialisation.</returns>
    private static UserLayoutPreferences FromDto(UserLayoutPreferencesSaveDto input)
    {
        var grids = new Dictionary<string, GridLayoutPreference>(StringComparer.OrdinalIgnoreCase);
        if (input.Grids is not null)
        {
            foreach (var kv in input.Grids)
            {
                if (kv.Value is null)
                {
                    continue;
                }
                grids[kv.Key] = new GridLayoutPreference
                {
                    VisibleColumns = kv.Value.VisibleColumns?.ToList() ?? [],
                    ColumnOrder = kv.Value.ColumnOrder?.ToList() ?? [],
                    PageSize = kv.Value.PageSize,
                };
            }
        }
        return new UserLayoutPreferences
        {
            Grids = grids,
            DefaultPageSize = input.DefaultPageSize,
            DashboardWidgetOrder = input.DashboardWidgetOrder?.ToList() ?? [],
        };
    }
}
