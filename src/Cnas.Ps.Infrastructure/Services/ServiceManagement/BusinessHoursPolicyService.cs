using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.ServiceManagement;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.ServiceManagement;

/// <summary>
/// R2501 / TOR PIR 024 — production implementation of
/// <see cref="IBusinessHoursPolicyService"/>. Owns CRUD over the
/// <c>BusinessHoursPolicy</c> registry plus the
/// <see cref="IsBusinessTimeAsync"/> / <see cref="AddBusinessDaysAsync"/>
/// helpers used by maintenance-window notice-lead-time enforcement.
/// </summary>
public sealed class BusinessHoursPolicyService : IBusinessHoursPolicyService
{
    /// <summary>Cached JSON serializer options shared across audit payloads.</summary>
    private static readonly JsonSerializerOptions CachedJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly ICnasDbContext _db;
    private readonly IReadOnlyCnasDbContext _read;
    private readonly ICnasTimeProvider _clock;
    private readonly ISqidService _sqids;
    private readonly ICallerContext _caller;
    private readonly IAuditService _audit;
    private readonly IValidator<BusinessHoursPolicyCreateInputDto> _createValidator;
    private readonly IValidator<BusinessHoursPolicyModifyInputDto> _modifyValidator;
    private readonly IValidator<BusinessHoursPolicyFilterDto> _filterValidator;

    /// <summary>Constructs the service with its scoped collaborators.</summary>
    /// <param name="db">Writer EF Core context.</param>
    /// <param name="read">Read-replica context.</param>
    /// <param name="clock">UTC clock abstraction.</param>
    /// <param name="sqids">Sqid encoder/decoder.</param>
    /// <param name="caller">Caller-context for audit attribution.</param>
    /// <param name="audit">Audit-journal façade.</param>
    /// <param name="createValidator">Validator for create input.</param>
    /// <param name="modifyValidator">Validator for modify input.</param>
    /// <param name="filterValidator">Validator for filter input.</param>
    public BusinessHoursPolicyService(
        ICnasDbContext db,
        IReadOnlyCnasDbContext read,
        ICnasTimeProvider clock,
        ISqidService sqids,
        ICallerContext caller,
        IAuditService audit,
        IValidator<BusinessHoursPolicyCreateInputDto> createValidator,
        IValidator<BusinessHoursPolicyModifyInputDto> modifyValidator,
        IValidator<BusinessHoursPolicyFilterDto> filterValidator)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(createValidator);
        ArgumentNullException.ThrowIfNull(modifyValidator);
        ArgumentNullException.ThrowIfNull(filterValidator);
        _db = db;
        _read = read;
        _clock = clock;
        _sqids = sqids;
        _caller = caller;
        _audit = audit;
        _createValidator = createValidator;
        _modifyValidator = modifyValidator;
        _filterValidator = filterValidator;
    }

    /// <inheritdoc />
    public async Task<Result<BusinessHoursPolicyDto>> CreateAsync(
        BusinessHoursPolicyCreateInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var v = await _createValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result<BusinessHoursPolicyDto>.Failure(ErrorCodes.ValidationFailed, v.Errors[0].ErrorMessage);
        }

        var duplicate = await _db.BusinessHoursPolicies
            .AnyAsync(p => p.Code == input.Code, cancellationToken)
            .ConfigureAwait(false);
        if (duplicate)
        {
            return Result<BusinessHoursPolicyDto>.Failure(
                ErrorCodes.BusinessHoursPolicyDuplicateCode,
                $"A business-hours policy with Code '{input.Code}' already exists.");
        }

        if (!TimeOnly.TryParse(input.OpenTimeLocal, CultureInfo.InvariantCulture, out var openTime))
        {
            return Result<BusinessHoursPolicyDto>.Failure(
                ErrorCodes.ValidationFailed, "OpenTimeLocal could not be parsed.");
        }
        if (!TimeOnly.TryParse(input.CloseTimeLocal, CultureInfo.InvariantCulture, out var closeTime))
        {
            return Result<BusinessHoursPolicyDto>.Failure(
                ErrorCodes.ValidationFailed, "CloseTimeLocal could not be parsed.");
        }
        if (closeTime <= openTime)
        {
            return Result<BusinessHoursPolicyDto>.Failure(
                ErrorCodes.ValidationFailed, "CloseTimeLocal must be strictly after OpenTimeLocal.");
        }

        var now = _clock.UtcNow;
        var actor = _caller.UserSqid ?? "admin";

        var policy = new BusinessHoursPolicy
        {
            Code = input.Code,
            DisplayName = input.DisplayName,
            Description = input.Description,
            OpenTimeLocal = openTime,
            CloseTimeLocal = closeTime,
            BusinessDaysMask = input.BusinessDaysMask,
            TimezoneId = input.TimezoneId,
            HolidayDatesJson = input.HolidayDatesJson,
            RegisteredByUserId = _caller.UserId ?? 0,
            CreatedAtUtc = now,
            CreatedBy = actor,
            IsActive = true,
        };
        _db.BusinessHoursPolicies.Add(policy);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        CnasMeter.BusinessHoursPolicyChanged.Add(1);

        await EmitAuditAsync(
            IBusinessHoursPolicyService.AuditPolicyCreated,
            AuditSeverity.Sensitive,
            actor,
            policy.Id,
            new
            {
                policySqid = _sqids.Encode(policy.Id),
                policy.Code,
                policy.TimezoneId,
                policy.BusinessDaysMask,
            },
            cancellationToken).ConfigureAwait(false);

        return Result<BusinessHoursPolicyDto>.Success(ToDto(policy));
    }

    /// <inheritdoc />
    public async Task<Result<BusinessHoursPolicyDto>> ModifyAsync(
        string policySqid,
        BusinessHoursPolicyModifyInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var v = await _modifyValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result<BusinessHoursPolicyDto>.Failure(ErrorCodes.ValidationFailed, v.Errors[0].ErrorMessage);
        }

        var loaded = await LoadAsync(policySqid, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return Result<BusinessHoursPolicyDto>.Failure(loaded.ErrorCode!, loaded.ErrorMessage!);
        }
        var policy = loaded.Value;

        if (input.DisplayName is not null) policy.DisplayName = input.DisplayName;
        if (input.Description is not null) policy.Description = input.Description;
        if (input.OpenTimeLocal is not null
            && TimeOnly.TryParse(input.OpenTimeLocal, CultureInfo.InvariantCulture, out var openTime))
        {
            policy.OpenTimeLocal = openTime;
        }
        if (input.CloseTimeLocal is not null
            && TimeOnly.TryParse(input.CloseTimeLocal, CultureInfo.InvariantCulture, out var closeTime))
        {
            policy.CloseTimeLocal = closeTime;
        }
        if (input.BusinessDaysMask is not null) policy.BusinessDaysMask = input.BusinessDaysMask.Value;
        if (input.TimezoneId is not null) policy.TimezoneId = input.TimezoneId;
        if (input.HolidayDatesJson is not null) policy.HolidayDatesJson = input.HolidayDatesJson;

        var now = _clock.UtcNow;
        var actor = _caller.UserSqid ?? "admin";
        policy.UpdatedAtUtc = now;
        policy.UpdatedBy = actor;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        CnasMeter.BusinessHoursPolicyChanged.Add(1);

        await EmitAuditAsync(
            IBusinessHoursPolicyService.AuditPolicyModified,
            AuditSeverity.Sensitive,
            actor,
            policy.Id,
            new
            {
                policySqid = _sqids.Encode(policy.Id),
                policy.Code,
                changeReason = input.ChangeReason,
            },
            cancellationToken).ConfigureAwait(false);

        return Result<BusinessHoursPolicyDto>.Success(ToDto(policy));
    }

    /// <inheritdoc />
    public Task<Result<BusinessHoursPolicyDto>> ActivateAsync(
        string policySqid,
        CancellationToken cancellationToken = default)
        => TransitionAsync(policySqid, newIsActive: true, transitionLabel: "Activate", cancellationToken);

    /// <inheritdoc />
    public Task<Result<BusinessHoursPolicyDto>> DeactivateAsync(
        string policySqid,
        CancellationToken cancellationToken = default)
        => TransitionAsync(policySqid, newIsActive: false, transitionLabel: "Deactivate", cancellationToken);

    /// <inheritdoc />
    public async Task<Result<BusinessHoursPolicyDto>> GetByIdAsync(
        string policySqid,
        CancellationToken cancellationToken = default)
    {
        var decoded = _sqids.TryDecode(policySqid);
        if (decoded.IsFailure)
        {
            return Result<BusinessHoursPolicyDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var row = await _read.BusinessHoursPolicies
            .FirstOrDefaultAsync(p => p.Id == decoded.Value, cancellationToken)
            .ConfigureAwait(false);
        return row is null
            ? Result<BusinessHoursPolicyDto>.Failure(
                ErrorCodes.BusinessHoursPolicyNotFound, "Business-hours policy not found.")
            : Result<BusinessHoursPolicyDto>.Success(ToDto(row));
    }

    /// <inheritdoc />
    public async Task<Result<BusinessHoursPolicyDto>> GetByCodeAsync(
        string policyCode,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(policyCode))
        {
            return Result<BusinessHoursPolicyDto>.Failure(ErrorCodes.ValidationFailed, "Code is required.");
        }
        var row = await _read.BusinessHoursPolicies
            .FirstOrDefaultAsync(p => p.Code == policyCode, cancellationToken)
            .ConfigureAwait(false);
        return row is null
            ? Result<BusinessHoursPolicyDto>.Failure(
                ErrorCodes.BusinessHoursPolicyNotFound, "Business-hours policy not found.")
            : Result<BusinessHoursPolicyDto>.Success(ToDto(row));
    }

    /// <inheritdoc />
    public async Task<Result<BusinessHoursPolicyPageDto>> ListAsync(
        BusinessHoursPolicyFilterDto filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var v = await _filterValidator.ValidateAsync(filter, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result<BusinessHoursPolicyPageDto>.Failure(ErrorCodes.ValidationFailed, v.Errors[0].ErrorMessage);
        }

        IQueryable<BusinessHoursPolicy> q = _read.BusinessHoursPolicies;
        if (filter.IsActive is not null)
        {
            var wantActive = filter.IsActive.Value;
            q = q.Where(p => p.IsActive == wantActive);
        }

        var total = await q.CountAsync(cancellationToken).ConfigureAwait(false);
        var rows = await q
            .OrderByDescending(p => p.CreatedAtUtc)
            .ThenByDescending(p => p.Id)
            .Skip(filter.Skip)
            .Take(filter.Take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var page = new BusinessHoursPolicyPageDto(
            Items: rows.Select(ToDto).ToList(),
            Total: total,
            Skip: filter.Skip,
            Take: filter.Take);
        return Result<BusinessHoursPolicyPageDto>.Success(page);
    }

    /// <inheritdoc />
    public async Task<Result<bool>> IsBusinessTimeAsync(
        string policyCode,
        DateTime utcInstant,
        CancellationToken cancellationToken = default)
    {
        var policy = await LoadByCodeAsync(policyCode, cancellationToken).ConfigureAwait(false);
        if (policy.IsFailure)
        {
            return Result<bool>.Failure(policy.ErrorCode!, policy.ErrorMessage!);
        }
        return Result<bool>.Success(IsBusinessTime(policy.Value, utcInstant));
    }

    /// <inheritdoc />
    public async Task<Result<DateTime>> AddBusinessDaysAsync(
        string policyCode,
        DateTime utcInstant,
        int businessDays,
        CancellationToken cancellationToken = default)
    {
        if (businessDays < 0)
        {
            return Result<DateTime>.Failure(ErrorCodes.ValidationFailed, "businessDays must be ≥ 0.");
        }
        var policy = await LoadByCodeAsync(policyCode, cancellationToken).ConfigureAwait(false);
        if (policy.IsFailure)
        {
            return Result<DateTime>.Failure(policy.ErrorCode!, policy.ErrorMessage!);
        }
        return Result<DateTime>.Success(AddBusinessDays(policy.Value, utcInstant, businessDays));
    }

    /// <summary>
    /// Pure helper that evaluates whether <paramref name="utcInstant"/> is a
    /// business instant for <paramref name="policy"/>.
    /// </summary>
    /// <param name="policy">Loaded policy.</param>
    /// <param name="utcInstant">Instant to test.</param>
    /// <returns><c>true</c> when the instant falls inside business hours.</returns>
    internal static bool IsBusinessTime(BusinessHoursPolicy policy, DateTime utcInstant)
    {
        var (localDate, localTime) = ToLocal(policy, utcInstant);
        if (!IsBusinessDay(policy, localDate))
        {
            return false;
        }
        return localTime >= policy.OpenTimeLocal && localTime < policy.CloseTimeLocal;
    }

    /// <summary>
    /// Adds <paramref name="businessDays"/> business days to
    /// <paramref name="utcInstant"/>, skipping non-business weekdays and
    /// holiday dates.
    /// </summary>
    /// <param name="policy">Loaded policy.</param>
    /// <param name="utcInstant">Starting UTC instant.</param>
    /// <param name="businessDays">Number of business days to add (≥ 0).</param>
    /// <returns>The shifted UTC instant.</returns>
    internal static DateTime AddBusinessDays(BusinessHoursPolicy policy, DateTime utcInstant, int businessDays)
    {
        // Convert to local date / time so we shift in the policy's calendar.
        var (localDate, localTime) = ToLocal(policy, utcInstant);
        var remaining = businessDays;
        var cursor = localDate;
        while (remaining > 0)
        {
            cursor = cursor.AddDays(1);
            if (IsBusinessDay(policy, cursor))
            {
                remaining--;
            }
        }
        // Reconstruct the UTC instant: same local clock time, new local date.
        var tz = ResolveTimezone(policy.TimezoneId);
        var resultLocal = new DateTime(cursor.Year, cursor.Month, cursor.Day, localTime.Hour, localTime.Minute, localTime.Second, DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(resultLocal, tz);
    }

    /// <summary>Converts a UTC instant to the policy's local date + time.</summary>
    /// <param name="policy">Loaded policy.</param>
    /// <param name="utcInstant">UTC instant.</param>
    /// <returns>Tuple of (local date, local clock time).</returns>
    private static (DateOnly LocalDate, TimeOnly LocalTime) ToLocal(BusinessHoursPolicy policy, DateTime utcInstant)
    {
        var tz = ResolveTimezone(policy.TimezoneId);
        var local = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(utcInstant, DateTimeKind.Utc), tz);
        return (DateOnly.FromDateTime(local), TimeOnly.FromDateTime(local));
    }

    /// <summary>Returns the IANA timezone, falling back to UTC when the id is unknown.</summary>
    /// <param name="timezoneId">IANA timezone id.</param>
    /// <returns>Resolved <see cref="TimeZoneInfo"/>.</returns>
    private static TimeZoneInfo ResolveTimezone(string timezoneId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timezoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    /// <summary>
    /// Returns true when <paramref name="localDate"/> is a business day per the
    /// policy's <c>BusinessDaysMask</c> AND is not listed in
    /// <c>HolidayDatesJson</c>.
    /// </summary>
    /// <param name="policy">Loaded policy.</param>
    /// <param name="localDate">Date to evaluate.</param>
    /// <returns>Boolean.</returns>
    private static bool IsBusinessDay(BusinessHoursPolicy policy, DateOnly localDate)
    {
        // Bit 0 = Mon, 1 = Tue, …, 6 = Sun. .NET's DayOfWeek puts Sunday at 0,
        // so convert via ((int)DayOfWeek + 6) % 7.
        var bitIndex = ((int)localDate.DayOfWeek + 6) % 7;
        var mask = 1 << bitIndex;
        if ((policy.BusinessDaysMask & mask) == 0)
        {
            return false;
        }
        if (string.IsNullOrWhiteSpace(policy.HolidayDatesJson))
        {
            return true;
        }
        var holidays = TryParseHolidays(policy.HolidayDatesJson);
        return !holidays.Contains(localDate);
    }

    /// <summary>Parses the JSON array of YYYY-MM-DD strings into a hash-set.</summary>
    /// <param name="json">Raw JSON.</param>
    /// <returns>Set of holidays (empty on parse failure).</returns>
    private static HashSet<DateOnly> TryParseHolidays(string json)
    {
        var result = new HashSet<DateOnly>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return result;
            }
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.String
                    && DateOnly.TryParse(element.GetString(), CultureInfo.InvariantCulture, out var parsed))
                {
                    result.Add(parsed);
                }
            }
        }
        catch (JsonException)
        {
            // Malformed JSON — treat as "no holidays configured".
        }
        return result;
    }

    /// <summary>Common helper for the Activate / Deactivate transitions.</summary>
    /// <param name="policySqid">Sqid of the policy to flip.</param>
    /// <param name="newIsActive">Desired IsActive flag.</param>
    /// <param name="transitionLabel">Audit-trail label.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Updated DTO on success.</returns>
    private async Task<Result<BusinessHoursPolicyDto>> TransitionAsync(
        string policySqid,
        bool newIsActive,
        string transitionLabel,
        CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(policySqid, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return Result<BusinessHoursPolicyDto>.Failure(loaded.ErrorCode!, loaded.ErrorMessage!);
        }
        var policy = loaded.Value;
        if (policy.IsActive == newIsActive)
        {
            return Result<BusinessHoursPolicyDto>.Failure(
                ErrorCodes.Conflict,
                newIsActive
                    ? "Policy is already Active."
                    : "Policy is already Inactive.");
        }

        var now = _clock.UtcNow;
        var actor = _caller.UserSqid ?? "admin";
        policy.IsActive = newIsActive;
        policy.UpdatedAtUtc = now;
        policy.UpdatedBy = actor;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        CnasMeter.BusinessHoursPolicyChanged.Add(1);

        await EmitAuditAsync(
            IBusinessHoursPolicyService.AuditPolicyTransitioned,
            AuditSeverity.Sensitive,
            actor,
            policy.Id,
            new
            {
                policySqid = _sqids.Encode(policy.Id),
                policy.Code,
                transition = transitionLabel,
                isActive = policy.IsActive,
                atUtc = now.ToString("O", CultureInfo.InvariantCulture),
            },
            cancellationToken).ConfigureAwait(false);

        return Result<BusinessHoursPolicyDto>.Success(ToDto(policy));
    }

    /// <summary>Loads a tracked policy entity by Sqid.</summary>
    /// <param name="policySqid">Sqid of the policy.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Tracked entity on success.</returns>
    private async Task<Result<BusinessHoursPolicy>> LoadAsync(string policySqid, CancellationToken cancellationToken)
    {
        var decoded = _sqids.TryDecode(policySqid);
        if (decoded.IsFailure)
        {
            return Result<BusinessHoursPolicy>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var row = await _db.BusinessHoursPolicies
            .FirstOrDefaultAsync(p => p.Id == decoded.Value, cancellationToken)
            .ConfigureAwait(false);
        return row is null
            ? Result<BusinessHoursPolicy>.Failure(
                ErrorCodes.BusinessHoursPolicyNotFound, "Business-hours policy not found.")
            : Result<BusinessHoursPolicy>.Success(row);
    }

    /// <summary>Loads a read-only policy by its stable code.</summary>
    /// <param name="policyCode">Stable code.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Entity on success.</returns>
    private async Task<Result<BusinessHoursPolicy>> LoadByCodeAsync(
        string policyCode,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(policyCode))
        {
            return Result<BusinessHoursPolicy>.Failure(
                ErrorCodes.ValidationFailed, "Code is required.");
        }
        var row = await _read.BusinessHoursPolicies
            .FirstOrDefaultAsync(p => p.Code == policyCode, cancellationToken)
            .ConfigureAwait(false);
        return row is null
            ? Result<BusinessHoursPolicy>.Failure(
                ErrorCodes.BusinessHoursPolicyNotFound, "Business-hours policy not found.")
            : Result<BusinessHoursPolicy>.Success(row);
    }

    /// <summary>Writes a single audit row with a serialised details payload.</summary>
    /// <param name="eventCode">Stable event code.</param>
    /// <param name="severity">Audit severity.</param>
    /// <param name="actor">Audit-attribution string.</param>
    /// <param name="targetEntityId">Database id of the affected row.</param>
    /// <param name="details">Arbitrary anonymous object serialised to JSON.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Completes when the audit row is enqueued.</returns>
    private async Task EmitAuditAsync(
        string eventCode,
        AuditSeverity severity,
        string actor,
        long targetEntityId,
        object details,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(details, CachedJsonOptions);
        await _audit.RecordAsync(
            eventCode,
            severity,
            actor,
            nameof(BusinessHoursPolicy),
            targetEntityId,
            json,
            _caller.SourceIp,
            _caller.CorrelationId,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Projects an entity into its outbound DTO.</summary>
    /// <param name="p">Loaded entity.</param>
    /// <returns>Populated DTO.</returns>
    private BusinessHoursPolicyDto ToDto(BusinessHoursPolicy p) => new(
        Id: _sqids.Encode(p.Id),
        Code: p.Code,
        DisplayName: p.DisplayName,
        Description: p.Description,
        OpenTimeLocal: p.OpenTimeLocal.ToString("HH:mm", CultureInfo.InvariantCulture),
        CloseTimeLocal: p.CloseTimeLocal.ToString("HH:mm", CultureInfo.InvariantCulture),
        BusinessDaysMask: p.BusinessDaysMask,
        TimezoneId: p.TimezoneId,
        HolidayDatesJson: p.HolidayDatesJson,
        IsActive: p.IsActive);
}
