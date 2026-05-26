using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Qbe;
using Cnas.Ps.Application.QueryBudget;
using Cnas.Ps.Application.Reports;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.Reports;

/// <summary>
/// R0156 / TOR CF 09.02 / FLEX 003 — default <see cref="IReportTemplateService"/>
/// implementation backed by <see cref="ICnasDbContext"/>. Implements the CRUD half
/// of the ad-hoc report builder; the execution half lives in
/// <see cref="ReportEngine"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Validation layers.</b> The wire-shape validator
/// (<see cref="ReportTemplateCreateDtoValidator"/>) runs at the MVC binding boundary.
/// Semantic invariants that depend on the live QBE registry schema (the
/// "every selected field must exist in the registry" check, the "GroupByField must
/// appear in the schema" check) run here so the service layer remains the only
/// place that has to invalidate when a schema changes.
/// </para>
/// <para>
/// <b>Audit shape.</b> Each successful create / update / delete emits a row through
/// <see cref="IAuditService"/> with the <c>REPORT_TEMPLATE.{CREATED|UPDATED|DELETED}</c>
/// event code at <see cref="AuditSeverity.Notice"/>. <c>DetailsJson</c> carries
/// <c>{ "code": ..., "registry": ... }</c>; the JSON payloads (filter, ordering,
/// selected fields) are intentionally omitted — they are reachable via the row
/// itself.
/// </para>
/// </remarks>
public sealed class ReportTemplateService(
    ICnasDbContext db,
    ICallerContext caller,
    ISqidService sqids,
    ICnasTimeProvider clock,
    IAuditService audit,
    IQbeRegistrySchemaProvider schemas)
    : IReportTemplateService
{
    private readonly ICnasDbContext _db = db;
    private readonly ICallerContext _caller = caller;
    private readonly ISqidService _sqids = sqids;
    private readonly ICnasTimeProvider _clock = clock;
    private readonly IAuditService _audit = audit;
    private readonly IQbeRegistrySchemaProvider _schemas = schemas;

    /// <summary>Stable event-code prefix for the audit trail.</summary>
    private const string AuditPrefix = "REPORT_TEMPLATE";

    /// <summary>JSON options used to round-trip the opaque payloads.</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    /// <inheritdoc />
    public async Task<Result<ReportTemplateDto>> CreateAsync(
        ReportTemplateCreateDto input,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (_caller.UserId is not long ownerId)
        {
            return Result<ReportTemplateDto>.Failure(ErrorCodes.Unauthorized, "Caller is not authenticated.");
        }

        // Code uniqueness — surface as Conflict so the caller can react with a "use a
        // different code" prompt rather than a generic validation error.
        var codeTaken = await _db.ReportTemplates
            .AnyAsync(t => t.IsActive && t.Code == input.Code, ct)
            .ConfigureAwait(false);
        if (codeTaken)
        {
            return Result<ReportTemplateDto>.Failure(
                ErrorCodes.Conflict,
                $"A report template with code '{input.Code}' already exists.");
        }

        var validation = ValidateAgainstSchema(input.Registry, input.SelectedFields, input.Filter, input.Ordering, input.GroupByField);
        if (validation is { } v)
        {
            return Result<ReportTemplateDto>.Failure(v.code, v.message);
        }

        var now = _clock.UtcNow;
        var row = new ReportTemplate
        {
            Code = input.Code,
            Name = input.Name,
            Description = input.Description,
            Registry = input.Registry,
            SelectedFieldsJson = JsonSerializer.Serialize(input.SelectedFields, JsonOptions),
            FilterJson = SerialiseFilter(input.Filter),
            OrderingJson = JsonSerializer.Serialize(input.Ordering, JsonOptions),
            GroupByField = input.GroupByField,
            OwnerUserId = ownerId,
            IsShared = input.IsShared,
            CreatedAtUtc = now,
            CreatedBy = _caller.UserSqid,
            IsActive = true,
        };

        _db.ReportTemplates.Add(row);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await EmitAuditAsync($"{AuditPrefix}.CREATED", row, ct).ConfigureAwait(false);

        return Result<ReportTemplateDto>.Success(Project(row));
    }

    /// <inheritdoc />
    public async Task<Result<ReportTemplateDto>> UpdateAsync(
        long templateId,
        ReportTemplateUpdateDto input,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (_caller.UserId is not long callerId)
        {
            return Result<ReportTemplateDto>.Failure(ErrorCodes.Unauthorized, "Caller is not authenticated.");
        }

        var row = await _db.ReportTemplates
            .SingleOrDefaultAsync(t => t.Id == templateId && t.IsActive, ct)
            .ConfigureAwait(false);
        if (row is null)
        {
            return Result<ReportTemplateDto>.Failure(ErrorCodes.NotFound, "Report template not found.");
        }

        if (row.OwnerUserId != callerId)
        {
            return Result<ReportTemplateDto>.Failure(
                ErrorCodes.Forbidden,
                "Only the owner may update a report template.");
        }

        var validation = ValidateAgainstSchema(row.Registry, input.SelectedFields, input.Filter, input.Ordering, input.GroupByField);
        if (validation is { } v)
        {
            return Result<ReportTemplateDto>.Failure(v.code, v.message);
        }

        var now = _clock.UtcNow;
        row.Name = input.Name;
        row.Description = input.Description;
        row.SelectedFieldsJson = JsonSerializer.Serialize(input.SelectedFields, JsonOptions);
        row.FilterJson = SerialiseFilter(input.Filter);
        row.OrderingJson = JsonSerializer.Serialize(input.Ordering, JsonOptions);
        row.GroupByField = input.GroupByField;
        row.IsShared = input.IsShared;
        row.UpdatedAtUtc = now;
        row.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await EmitAuditAsync($"{AuditPrefix}.UPDATED", row, ct).ConfigureAwait(false);

        return Result<ReportTemplateDto>.Success(Project(row));
    }

    /// <inheritdoc />
    public async Task<Result> DeleteAsync(long templateId, CancellationToken ct = default)
    {
        if (_caller.UserId is not long callerId)
        {
            return Result.Failure(ErrorCodes.Unauthorized, "Caller is not authenticated.");
        }

        var row = await _db.ReportTemplates
            .SingleOrDefaultAsync(t => t.Id == templateId && t.IsActive, ct)
            .ConfigureAwait(false);
        if (row is null)
        {
            return Result.Failure(ErrorCodes.NotFound, "Report template not found.");
        }

        if (row.OwnerUserId != callerId)
        {
            return Result.Failure(ErrorCodes.Forbidden, "Only the owner may delete a report template.");
        }

        var now = _clock.UtcNow;
        row.IsActive = false;
        row.UpdatedAtUtc = now;
        row.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await EmitAuditAsync($"{AuditPrefix}.DELETED", row, ct).ConfigureAwait(false);

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<ReportTemplateDto?> GetAsync(long templateId, CancellationToken ct = default)
    {
        if (_caller.UserId is not long callerId)
        {
            return null;
        }

        var row = await _db.ReportTemplates
            .SingleOrDefaultAsync(t => t.Id == templateId && t.IsActive, ct)
            .ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }
        if (row.OwnerUserId != callerId && !row.IsShared)
        {
            return null;
        }
        return Project(row);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReportTemplateDto>> ListAccessibleAsync(CancellationToken ct = default)
    {
        if (_caller.UserId is not long callerId)
        {
            return Array.Empty<ReportTemplateDto>();
        }

        var rows = await _db.ReportTemplates
            .Where(t => t.IsActive && (t.OwnerUserId == callerId || t.IsShared))
            .OrderBy(t => t.Name)
            .ToListAsync(ct).ConfigureAwait(false);

        return rows.Select(Project).ToList();
    }

    /// <summary>
    /// Runs the schema-aware semantic validation that the wire-shape validator cannot:
    /// every selected field must exist in the registry's QBE schema, the group-by
    /// field (when set) must be one of the selected fields AND exist in the schema,
    /// and every ordering field must exist in the schema.
    /// </summary>
    /// <param name="registry">Registry code (must be registered with the schema provider).</param>
    /// <param name="selectedFields">Field-name projection list.</param>
    /// <param name="filter">QBE envelope (not deeply validated here — converter does that).</param>
    /// <param name="ordering">Ordering specifications.</param>
    /// <param name="groupByField">Optional group-by field.</param>
    /// <returns>
    /// <c>null</c> when every check passes; otherwise a (code, message) tuple ready to
    /// be lifted into a <see cref="Result{T}.Failure"/>.
    /// </returns>
    private (string code, string message)? ValidateAgainstSchema(
        string registry,
        IReadOnlyList<string> selectedFields,
        QbeFilterDto filter,
        IReadOnlyList<ReportOrderingDto> ordering,
        string? groupByField)
    {
        var schema = _schemas.GetForRegistry(registry);
        if (schema is null)
        {
            return (ErrorCodes.QbeRegistryUnknown, $"Unknown registry '{registry}'.");
        }
        for (var i = 0; i < selectedFields.Count; i++)
        {
            if (schema.FindField(selectedFields[i]) is null)
            {
                return (ErrorCodes.QbeFieldNotQueryable,
                    $"Field '{selectedFields[i]}' is not declared on registry '{registry}'.");
            }
        }
        for (var i = 0; i < ordering.Count; i++)
        {
            var spec = ordering[i];
            if (schema.FindField(spec.Field) is null)
            {
                return (ErrorCodes.QbeFieldNotQueryable,
                    $"Ordering field '{spec.Field}' is not declared on registry '{registry}'.");
            }
            // Direction sanity — case-insensitive Asc/Desc.
            if (!string.Equals(spec.Direction, ReportOrderingDto.Asc, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(spec.Direction, ReportOrderingDto.Desc, StringComparison.OrdinalIgnoreCase))
            {
                return (ErrorCodes.ValidationFailed,
                    $"Ordering direction '{spec.Direction}' must be ASC or DESC.");
            }
        }
        if (!string.IsNullOrEmpty(groupByField))
        {
            if (schema.FindField(groupByField) is null)
            {
                return (ErrorCodes.QbeFieldNotQueryable,
                    $"GroupBy field '{groupByField}' is not declared on registry '{registry}'.");
            }
            // The wire-shape validator already enforces that GroupByField is one of
            // SelectedFields — defence in depth so a malformed service-layer call still
            // refuses the request.
            if (!selectedFields.Contains(groupByField, StringComparer.Ordinal))
            {
                return (ErrorCodes.ValidationFailed,
                    "GroupByField must also appear in SelectedFields.");
            }
        }
        // ArgumentNullException-style guard on the filter envelope — its inner shape is
        // validated by the converter at run time so the validator does not need to
        // double up here.
        ArgumentNullException.ThrowIfNull(filter);
        return null;
    }

    /// <summary>
    /// Serialises a wire <see cref="QbeFilterDto"/> to the persisted JSON string. The
    /// shape mirrors the wire DTO byte-for-byte so a future debug session can pretty-
    /// print the column directly.
    /// </summary>
    /// <param name="filter">Wire envelope; non-null.</param>
    /// <returns>The serialised JSON.</returns>
    private static string SerialiseFilter(QbeFilterDto filter) =>
        JsonSerializer.Serialize(filter, JsonOptions);

    /// <summary>Projects a persisted row into its public DTO form.</summary>
    /// <param name="row">Persisted template.</param>
    /// <returns>The DTO surfaced through the API.</returns>
    private ReportTemplateDto Project(ReportTemplate row)
    {
        var selected = JsonSerializer.Deserialize<List<string>>(row.SelectedFieldsJson, JsonOptions)
            ?? new List<string>();
        var ordering = JsonSerializer.Deserialize<List<ReportOrderingDto>>(row.OrderingJson, JsonOptions)
            ?? new List<ReportOrderingDto>();
        var filter = JsonSerializer.Deserialize<QbeFilterDto>(row.FilterJson, JsonOptions)
            ?? new QbeFilterDto(QbeFilter.CombinatorAnd, Array.Empty<QbeConditionDto>());
        return new ReportTemplateDto(
            Id: _sqids.Encode(row.Id),
            Code: row.Code,
            Name: row.Name,
            Description: row.Description,
            Registry: row.Registry,
            SelectedFields: selected,
            Filter: filter,
            Ordering: ordering,
            GroupByField: row.GroupByField,
            OwnerUserSqid: _sqids.Encode(row.OwnerUserId),
            IsShared: row.IsShared);
    }

    /// <summary>
    /// Emits an audit-trail row for a template create/update/delete. The
    /// <c>DetailsJson</c> intentionally carries the stable identifying fields only
    /// (code, registry) — the JSON payloads are deliberately excluded so a citizen's
    /// identifier inadvertently captured in a filter cannot leak through the audit
    /// trail.
    /// </summary>
    /// <param name="eventCode">Stable event code (e.g. <c>REPORT_TEMPLATE.CREATED</c>).</param>
    /// <param name="row">The persisted (or just-modified) template row.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task EmitAuditAsync(string eventCode, ReportTemplate row, CancellationToken ct)
    {
        var details = JsonSerializer.Serialize(new
        {
            code = row.Code,
            registry = row.Registry,
        });
        var actor = _caller.UserSqid ?? "system";
        await _audit.RecordAsync(
            eventCode: eventCode,
            severity: AuditSeverity.Notice,
            actorId: actor,
            targetEntity: nameof(ReportTemplate),
            targetEntityId: row.Id,
            detailsJson: details,
            sourceIp: _caller.SourceIp,
            correlationId: _caller.CorrelationId,
            cancellationToken: ct).ConfigureAwait(false);
    }
}
