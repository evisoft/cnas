using System.Diagnostics;
using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Etl;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cnas.Ps.Infrastructure.Services.Etl;

/// <summary>
/// R0153 / TOR CF 19.05 — default implementation of
/// <see cref="IContributorPeriodProjectionService"/>. Reads every
/// supersession child row for an <see cref="InsuredPerson"/> ("Persoană
/// asigurată"), flattens them via <see cref="PeriodSliceBuilder"/>, and
/// DELETE-then-INSERTs the resulting slices into the
/// <see cref="ContributorPeriodProjection"/> table.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifetime.</b> Scoped — captures the per-request DbContext for the
/// write path. The job composition wires a fresh scope per fire so the
/// service can be invoked from a background <see cref="Quartz.IJob"/>
/// without leaking state.
/// </para>
/// <para>
/// <b>Field-name vocabulary.</b> The closed set of field names piped through
/// the slice builder is held in <see cref="FieldNames"/>. Adding a new
/// projected field requires:
/// </para>
/// <list type="number">
///   <item><description>Add the field to <see cref="ContributorPeriodProjection"/>.</description></item>
///   <item><description>Add the field name to <see cref="FieldNames"/>.</description></item>
///   <item><description>Append the source-row loader in <see cref="LoadSourceRowsAsync"/>.</description></item>
///   <item><description>Append the value-pull in <see cref="ProjectSliceToEntity"/>.</description></item>
/// </list>
/// <para>
/// <b>Audit contract.</b> The batch <see cref="RebuildAllAsync"/> writes
/// exactly one <c>ETL.PERIOD_PROJECTION.COMPLETED</c> audit row at
/// <see cref="AuditSeverity.Information"/> per run; the per-contributor
/// <see cref="RebuildForContributorAsync"/> does NOT audit on its own
/// because the high-volume batch path would generate excessive noise
/// otherwise.
/// </para>
/// </remarks>
public sealed class ContributorPeriodProjectionService : IContributorPeriodProjectionService
{
    /// <summary>Stable actor id stamped on every audit row emitted by this service.</summary>
    private const string SystemActor = "system:contributor-projection";

    /// <summary>Stable audit event code for a completed batch run.</summary>
    private const string AuditEventCode = "ETL.PERIOD_PROJECTION.COMPLETED";

    /// <summary>Field name — CivilStatus column on the projection.</summary>
    private const string CivilStatusField = "CivilStatus";

    /// <summary>Field name — CurrentEmployerCode column on the projection.</summary>
    private const string CurrentEmployerCodeField = "CurrentEmployerCode";

    /// <summary>Field name — MonthlySalary column on the projection.</summary>
    private const string MonthlySalaryField = "MonthlySalary";

    /// <summary>Field name — AddressCity column on the projection.</summary>
    private const string AddressCityField = "AddressCity";

    /// <summary>Field name — AddressRegion column on the projection.</summary>
    private const string AddressRegionField = "AddressRegion";

    /// <summary>Field name — AddressCountry column on the projection.</summary>
    private const string AddressCountryField = "AddressCountry";

    /// <summary>Field name — PhoneE164 column on the projection.</summary>
    private const string PhoneE164Field = "PhoneE164";

    /// <summary>Field name — Email column on the projection.</summary>
    private const string EmailField = "Email";

    /// <summary>
    /// Closed set of field names piped through <see cref="PeriodSliceBuilder"/>.
    /// Stable across runs — adding or removing a name is a breaking change to
    /// the projection contract (every slice carries one value per name).
    /// </summary>
    private static readonly string[] FieldNames =
    {
        CivilStatusField,
        CurrentEmployerCodeField,
        MonthlySalaryField,
        AddressCityField,
        AddressRegionField,
        AddressCountryField,
        PhoneE164Field,
        EmailField,
    };

    private readonly ICnasDbContext _writeDb;
    private readonly ICnasTimeProvider _clock;
    private readonly ISqidService _sqids;
    private readonly IAuditService _audit;
    private readonly ILogger<ContributorPeriodProjectionService> _logger;

    /// <summary>Constructs the service with its scoped collaborators.</summary>
    /// <param name="writeDb">Primary DbContext used for both read and write paths.</param>
    /// <param name="clock">UTC clock — supplies <c>ProjectedAtUtc</c> + audit stamps.</param>
    /// <param name="sqids">Sqid encoder for the run surrogate id.</param>
    /// <param name="audit">Audit facade — writes one row per batch run.</param>
    /// <param name="logger">Structured logger.</param>
    public ContributorPeriodProjectionService(
        ICnasDbContext writeDb,
        ICnasTimeProvider clock,
        ISqidService sqids,
        IAuditService audit,
        ILogger<ContributorPeriodProjectionService> logger)
    {
        ArgumentNullException.ThrowIfNull(writeDb);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(logger);

        _writeDb = writeDb;
        _clock = clock;
        _sqids = sqids;
        _audit = audit;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<ContributorPeriodProjectionRunDto>> RebuildForContributorAsync(
        long contributorId, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var slicesCreated = await RebuildOneAsync(contributorId, ct).ConfigureAwait(false);
        await _writeDb.SaveChangesAsync(ct).ConfigureAwait(false);
        sw.Stop();

        CnasMeter.ContributorProjectionSlices.Add(slicesCreated);

        _logger.LogInformation(
            "ContributorPeriodProjection rebuilt for contributorId={ContributorId}: slices={Slices} duration={DurationMs}ms",
            contributorId, slicesCreated, sw.ElapsedMilliseconds);

        return Result<ContributorPeriodProjectionRunDto>.Success(
            new ContributorPeriodProjectionRunDto(
                ContributorSqid: _sqids.Encode(contributorId),
                SlicesCreated: slicesCreated,
                ContributorsProcessed: 1,
                DurationMs: sw.ElapsedMilliseconds));
    }

    /// <inheritdoc />
    public async Task<Result<ContributorPeriodProjectionRunDto>> RebuildAllAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        var contributorIds = await _writeDb.InsuredPersons
            .Select(p => p.Id)
            .ToListAsync(ct).ConfigureAwait(false);

        var totalSlices = 0;
        var processed = 0;

        foreach (var id in contributorIds)
        {
            ct.ThrowIfCancellationRequested();
            totalSlices += await RebuildOneAsync(id, ct).ConfigureAwait(false);
            processed += 1;
        }

        await _writeDb.SaveChangesAsync(ct).ConfigureAwait(false);
        sw.Stop();

        CnasMeter.ContributorProjectionRun.Add(1,
            new KeyValuePair<string, object?>("outcome", "success"));
        CnasMeter.ContributorProjectionSlices.Add(totalSlices);

        var detailsJson = JsonSerializer.Serialize(new
        {
            contributorsProcessed = processed,
            slicesCreated = totalSlices,
            durationMs = sw.ElapsedMilliseconds,
        });

        await _audit.RecordAsync(
            eventCode: AuditEventCode,
            severity: AuditSeverity.Information,
            actorId: SystemActor,
            targetEntity: nameof(ContributorPeriodProjection),
            targetEntityId: null,
            detailsJson: detailsJson,
            sourceIp: null,
            correlationId: null,
            cancellationToken: ct).ConfigureAwait(false);

        _logger.LogInformation(
            "ContributorPeriodProjection batch completed: contributors={Count} slices={Slices} duration={DurationMs}ms",
            processed, totalSlices, sw.ElapsedMilliseconds);

        return Result<ContributorPeriodProjectionRunDto>.Success(
            new ContributorPeriodProjectionRunDto(
                ContributorSqid: null,
                SlicesCreated: totalSlices,
                ContributorsProcessed: processed,
                DurationMs: sw.ElapsedMilliseconds));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ContributorPeriodProjectionDto>> QueryAsync(
        long contributorId, DateTime asOfUtc, CancellationToken ct)
    {
        var rows = await _writeDb.ContributorPeriodProjections
            .AsNoTracking()
            .Where(p => p.ContributorId == contributorId
                && p.PeriodStartUtc <= asOfUtc
                && asOfUtc < p.PeriodEndUtc)
            .OrderBy(p => p.PeriodStartUtc)
            .ToListAsync(ct).ConfigureAwait(false);

        return rows.Select(r => new ContributorPeriodProjectionDto(
            Id: _sqids.Encode(r.Id),
            ContributorSqid: _sqids.Encode(r.ContributorId),
            PeriodStartUtc: r.PeriodStartUtc,
            PeriodEndUtc: r.PeriodEndUtc,
            CivilStatus: r.CivilStatus,
            CurrentEmployerCode: r.CurrentEmployerCode,
            MonthlySalary: r.MonthlySalary,
            AddressCity: r.AddressCity,
            AddressRegion: r.AddressRegion,
            AddressCountry: r.AddressCountry,
            PhoneE164: r.PhoneE164,
            Email: r.Email,
            ProjectedAtUtc: r.ProjectedAtUtc)).ToList();
    }

    /// <summary>
    /// Rebuilds the projection rows for a single contributor in the caller's
    /// transaction. The caller is responsible for the final
    /// <see cref="ICnasDbContext.SaveChangesAsync"/>.
    /// </summary>
    /// <param name="contributorId">Internal raw id of the InsuredPerson.</param>
    /// <param name="ct">Cooperative cancellation token.</param>
    /// <returns>Number of projection slices written for the contributor.</returns>
    private async Task<int> RebuildOneAsync(long contributorId, CancellationToken ct)
    {
        // 1) Wipe prior projection rows for the contributor — idempotent rebuild.
        var existing = await _writeDb.ContributorPeriodProjections
            .Where(p => p.ContributorId == contributorId)
            .ToListAsync(ct).ConfigureAwait(false);
        if (existing.Count > 0)
        {
            _writeDb.ContributorPeriodProjections.RemoveRange(existing);
        }

        // 2) Load every relevant source row, flatten into SourceRow records.
        var sourceRows = await LoadSourceRowsAsync(contributorId, ct).ConfigureAwait(false);
        if (sourceRows.Count == 0)
        {
            return 0;
        }

        // 3) Slice-build.
        var slices = PeriodSliceBuilder.Build(sourceRows, FieldNames);
        if (slices.Count == 0)
        {
            return 0;
        }

        // 4) Project each slice into an entity row.
        var now = _clock.UtcNow;
        var entities = slices
            .Select(s => ProjectSliceToEntity(contributorId, s, now))
            .ToList();
        await _writeDb.ContributorPeriodProjections
            .AddRangeAsync(entities, ct).ConfigureAwait(false);

        return entities.Count;
    }

    /// <summary>
    /// Loads every relevant supersession row for the contributor and emits a
    /// flat list of <see cref="PeriodSliceBuilder.SourceRow"/> records keyed by
    /// the projection's field-name vocabulary. Multiple source columns contribute
    /// to a single field (e.g. <c>ContributorAddress.City</c> contributes to
    /// <see cref="AddressCityField"/>) — the loader writes one source row per
    /// contributing column-row.
    /// </summary>
    /// <param name="contributorId">Internal raw id of the InsuredPerson.</param>
    /// <param name="ct">Cooperative cancellation token.</param>
    /// <returns>Flat list of source rows ready for <see cref="PeriodSliceBuilder.Build"/>.</returns>
    private async Task<List<PeriodSliceBuilder.SourceRow>> LoadSourceRowsAsync(
        long contributorId, CancellationToken ct)
    {
        var rows = new List<PeriodSliceBuilder.SourceRow>();

        var addresses = await _writeDb.ContributorAddresses
            .Where(a => a.ContributorId == contributorId)
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var a in addresses)
        {
            rows.Add(new(a.Id, AddressCityField, a.City, a.ValidFromUtc, a.ValidToUtc, a.CreatedAtUtc));
            rows.Add(new(a.Id, AddressRegionField, a.Region, a.ValidFromUtc, a.ValidToUtc, a.CreatedAtUtc));
            rows.Add(new(a.Id, AddressCountryField, a.Country, a.ValidFromUtc, a.ValidToUtc, a.CreatedAtUtc));
        }

        var contacts = await _writeDb.ContributorContacts
            .Where(c => c.ContributorId == contributorId)
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var c in contacts)
        {
            rows.Add(new(c.Id, PhoneE164Field, c.PhoneE164, c.ValidFromUtc, c.ValidToUtc, c.CreatedAtUtc));
            rows.Add(new(c.Id, EmailField, c.Email, c.ValidFromUtc, c.ValidToUtc, c.CreatedAtUtc));
        }

        var activityPeriods = await _writeDb.ContributorActivityPeriods
            .Where(p => p.ContributorId == contributorId)
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var p in activityPeriods)
        {
            rows.Add(new(p.Id, CurrentEmployerCodeField, p.EmployerCode, p.ValidFromUtc, p.ValidToUtc, p.CreatedAtUtc));
            rows.Add(new(p.Id, MonthlySalaryField, p.MonthlySalary, p.ValidFromUtc, p.ValidToUtc, p.CreatedAtUtc));
        }

        var civilStatuses = await _writeDb.ContributorCivilStatuses
            .Where(s => s.ContributorId == contributorId)
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var s in civilStatuses)
        {
            rows.Add(new(s.Id, CivilStatusField, s.Status.ToString(), s.ValidFromUtc, s.ValidToUtc, s.CreatedAtUtc));
        }

        return rows;
    }

    /// <summary>
    /// Materialises a single <see cref="PeriodSlice{TSource}"/> into a fresh
    /// <see cref="ContributorPeriodProjection"/> entity using the
    /// <see cref="FieldNames"/> vocabulary as the key set.
    /// </summary>
    /// <param name="contributorId">FK to the source InsuredPerson.</param>
    /// <param name="slice">Slice the builder emitted.</param>
    /// <param name="now">UTC stamp for the projection's audit columns.</param>
    /// <returns>A ready-to-insert projection entity.</returns>
    private static ContributorPeriodProjection ProjectSliceToEntity(
        long contributorId, PeriodSlice<object> slice, DateTime now)
    {
        return new ContributorPeriodProjection
        {
            ContributorId = contributorId,
            PeriodStartUtc = slice.PeriodStartUtc,
            PeriodEndUtc = slice.PeriodEndUtc,
            CivilStatus = slice.ResolvedFields[CivilStatusField] as string,
            CurrentEmployerCode = slice.ResolvedFields[CurrentEmployerCodeField] as string,
            MonthlySalary = slice.ResolvedFields[MonthlySalaryField] as decimal?,
            AddressCity = slice.ResolvedFields[AddressCityField] as string,
            AddressRegion = slice.ResolvedFields[AddressRegionField] as string,
            AddressCountry = slice.ResolvedFields[AddressCountryField] as string,
            PhoneE164 = slice.ResolvedFields[PhoneE164Field] as string,
            Email = slice.ResolvedFields[EmailField] as string,
            ProjectedAtUtc = now,
            CreatedAtUtc = now,
            CreatedBy = SystemActor,
            IsActive = true,
        };
    }
}
