using System.Globalization;
using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.LaborBooklet;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.LaborBooklet;

/// <summary>
/// R0922 / TOR Annex 2 §8.2.4 — concrete implementation of
/// <see cref="IPre1999StagiuService"/>. Owns the InsuredPerson-attached
/// pre-1999 stagiu roll-up table.
/// </summary>
public sealed class Pre1999StagiuService : IPre1999StagiuService
{
    /// <summary>Stable audit event code emitted by <see cref="AppendAsync"/>.</summary>
    public const string AuditAppended = "PRE1999_STAGIU.APPENDED";

    /// <summary>Stable audit event code emitted by <see cref="RemoveAsync"/>.</summary>
    public const string AuditRemoved = "PRE1999_STAGIU.REMOVED";

    private readonly ICnasDbContext _db;
    private readonly ICnasTimeProvider _clock;
    private readonly ISqidService _sqids;
    private readonly ICallerContext _caller;
    private readonly IAuditService _audit;
    private readonly IValidator<Pre1999StagiuInputDto> _validator;

    /// <summary>Constructs the service with its collaborators.</summary>
    /// <param name="db">EF Core context abstraction.</param>
    /// <param name="clock">UTC clock — never call <see cref="DateTime.UtcNow"/> directly.</param>
    /// <param name="sqids">Sqid encoder/decoder.</param>
    /// <param name="caller">Authenticated caller information for audit attribution.</param>
    /// <param name="audit">Audit journal façade.</param>
    /// <param name="validator">Validator for the input envelope.</param>
    public Pre1999StagiuService(
        ICnasDbContext db,
        ICnasTimeProvider clock,
        ISqidService sqids,
        ICallerContext caller,
        IAuditService audit,
        IValidator<Pre1999StagiuInputDto> validator)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(validator);

        _db = db;
        _clock = clock;
        _sqids = sqids;
        _caller = caller;
        _audit = audit;
        _validator = validator;
    }

    /// <inheritdoc />
    public async Task<Result<Pre1999StagiuDto>> AppendAsync(
        string insuredSqid,
        Pre1999StagiuInputDto input,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var decoded = _sqids.TryDecode(insuredSqid);
        if (decoded.IsFailure)
        {
            return Result<Pre1999StagiuDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        var validation = await _validator.ValidateAsync(input, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            var first = validation.Errors.Count > 0 ? validation.Errors[0].ErrorMessage : "Invalid input.";
            return Result<Pre1999StagiuDto>.Failure(ErrorCodes.ValidationFailed, first);
        }

        var insured = await _db.InsuredPersons.AsNoTracking()
            .SingleOrDefaultAsync(p => p.Id == decoded.Value && p.IsActive, ct)
            .ConfigureAwait(false);
        if (insured is null)
        {
            return Result<Pre1999StagiuDto>.Failure(
                ErrorCodes.NotFound,
                $"InsuredPerson id={decoded.Value} not found.");
        }

        var now = _clock.UtcNow;
        var row = new Pre1999StagiuRecord
        {
            InsuredPersonId = decoded.Value,
            FromDate = input.FromDate,
            ToDate = input.ToDate,
            Years = input.Years,
            Months = input.Months,
            Days = input.Days,
            Source = input.Source,
            Notes = input.Notes,
            CreatedAtUtc = now,
            CreatedBy = _caller.UserSqid,
            IsActive = true,
        };
        _db.Pre1999StagiuRecords.Add(row);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var detailsJson = JsonSerializer.Serialize(new
        {
            insuredPersonId = decoded.Value,
            fromDate = input.FromDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            toDate = input.ToDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            input.Years,
            input.Months,
            input.Days,
        });
        await _audit.RecordAsync(
            AuditAppended,
            AuditSeverity.Notice,
            _caller.UserSqid ?? "?",
            nameof(Pre1999StagiuRecord),
            row.Id,
            detailsJson,
            _caller.SourceIp,
            _caller.CorrelationId,
            ct).ConfigureAwait(false);

        return Result<Pre1999StagiuDto>.Success(Project(row, insuredSqid));
    }

    /// <inheritdoc />
    public async Task<Result> RemoveAsync(
        string recordSqid,
        CancellationToken ct = default)
    {
        var decoded = _sqids.TryDecode(recordSqid);
        if (decoded.IsFailure)
        {
            return Result.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        var row = await _db.Pre1999StagiuRecords
            .SingleOrDefaultAsync(r => r.Id == decoded.Value && r.IsActive, ct)
            .ConfigureAwait(false);
        if (row is null)
        {
            return Result.Failure(
                ErrorCodes.NotFound,
                $"Pre1999StagiuRecord id={decoded.Value} not found.");
        }

        var now = _clock.UtcNow;
        row.IsActive = false;
        row.UpdatedAtUtc = now;
        row.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var detailsJson = JsonSerializer.Serialize(new
        {
            recordId = row.Id,
            insuredPersonId = row.InsuredPersonId,
        });
        await _audit.RecordAsync(
            AuditRemoved,
            AuditSeverity.Notice,
            _caller.UserSqid ?? "?",
            nameof(Pre1999StagiuRecord),
            row.Id,
            detailsJson,
            _caller.SourceIp,
            _caller.CorrelationId,
            ct).ConfigureAwait(false);

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<Pre1999StagiuDto>>> ListAsync(
        string insuredSqid,
        CancellationToken ct = default)
    {
        var decoded = _sqids.TryDecode(insuredSqid);
        if (decoded.IsFailure)
        {
            return Result<IReadOnlyList<Pre1999StagiuDto>>.Failure(
                decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        var insuredExists = await _db.InsuredPersons.AsNoTracking()
            .AnyAsync(p => p.Id == decoded.Value && p.IsActive, ct)
            .ConfigureAwait(false);
        if (!insuredExists)
        {
            return Result<IReadOnlyList<Pre1999StagiuDto>>.Failure(
                ErrorCodes.NotFound,
                $"InsuredPerson id={decoded.Value} not found.");
        }

        var rows = await _db.Pre1999StagiuRecords.AsNoTracking()
            .Where(r => r.InsuredPersonId == decoded.Value && r.IsActive)
            .OrderBy(r => r.FromDate)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        IReadOnlyList<Pre1999StagiuDto> projected = rows
            .Select(r => Project(r, insuredSqid))
            .ToList();
        return Result<IReadOnlyList<Pre1999StagiuDto>>.Success(projected);
    }

    /// <summary>Projects the entity into the wire DTO with the supplied (already-Sqid) parent reference.</summary>
    private Pre1999StagiuDto Project(Pre1999StagiuRecord row, string insuredSqid) => new(
        Id: _sqids.Encode(row.Id),
        InsuredPersonSqid: insuredSqid,
        FromDate: row.FromDate,
        ToDate: row.ToDate,
        Years: row.Years,
        Months: row.Months,
        Days: row.Days,
        Source: row.Source,
        Notes: row.Notes);
}
