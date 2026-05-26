using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Templates;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.Templates;

/// <summary>
/// R2003 / R0133 — Concrete <see cref="ITemplateLanguageCoverageService"/>.
/// Owns the pure-read coverage projection, the persisted-finding scan path,
/// the operator acknowledgement, and the findings list endpoint.
/// </summary>
/// <remarks>
/// <para>
/// <b>Coverage algorithm.</b> The projection iterates every (optionally
/// active-only) <see cref="DocumentTemplate"/> row, fetches the associated
/// <see cref="TemplateVariant"/> rows (filtered to <c>IsActive=true</c>),
/// and builds three per-template sets: approved-languages, unapproved-languages,
/// and the union. The "missing" set is then computed as
/// (required − (approved OR union)) depending on the <c>OnlyApproved</c>
/// filter. Gaps are sorted alphabetically by template code so the operator
/// UI's column order is deterministic across runs.
/// </para>
/// <para>
/// <b>Idempotent persistence.</b>
/// <see cref="RecordCoverageRunAsync"/> uses the filtered unique index on
/// <c>(TemplateId, MissingLanguage, Acknowledged=false)</c> to dedupe open
/// findings. The service pre-loads the existing open-finding set before
/// inserting so a re-run of the same scan against a stable database is a
/// no-op (no new inserts, no new audit events, no metric ticks).
/// </para>
/// </remarks>
public sealed class TemplateLanguageCoverageService : ITemplateLanguageCoverageService
{
    /// <summary>Stable audit code emitted once per new finding inserted.</summary>
    public const string AuditGapDetected = "TEMPLATE.COVERAGE.GAP_DETECTED";

    /// <summary>Stable audit code emitted on finding acknowledgement.</summary>
    public const string AuditGapAcknowledged = "TEMPLATE.COVERAGE.GAP_ACKNOWLEDGED";

    /// <summary>Default required-language set when the filter envelope leaves <c>RequiredLanguages</c> empty.</summary>
    private static readonly IReadOnlyList<string> CanonicalRequiredLanguages =
    [
        TemplateLanguages.Ro,
        TemplateLanguages.En,
        TemplateLanguages.Ru,
    ];

    private readonly ICnasDbContext _db;
    private readonly IAuditService _audit;
    private readonly ISqidService _sqids;
    private readonly ICnasTimeProvider _clock;
    private readonly ICallerContext _caller;
    private readonly IValidator<TemplateLanguageCoverageFilterDto> _filterValidator;
    private readonly IValidator<TemplateLanguageCoverageFindingFilterDto> _findingFilterValidator;
    private readonly IValidator<TemplateLanguageCoverageAcknowledgeInputDto> _ackValidator;

    /// <summary>Constructs the service with its scoped collaborators.</summary>
    /// <param name="db">Writer DB context.</param>
    /// <param name="audit">Audit service — emits the gap-detected + gap-acknowledged rows.</param>
    /// <param name="sqids">Sqid encoder/decoder for boundary id translation.</param>
    /// <param name="clock">UTC clock abstraction (CLAUDE.md RULE 4).</param>
    /// <param name="caller">Caller context used to attribute acknowledgements.</param>
    /// <param name="filterValidator">Validator for the coverage-filter envelope.</param>
    /// <param name="findingFilterValidator">Validator for the findings-list filter envelope.</param>
    /// <param name="ackValidator">Validator for the acknowledgement payload.</param>
    /// <exception cref="ArgumentNullException">When a required collaborator is null.</exception>
    public TemplateLanguageCoverageService(
        ICnasDbContext db,
        IAuditService audit,
        ISqidService sqids,
        ICnasTimeProvider clock,
        ICallerContext caller,
        IValidator<TemplateLanguageCoverageFilterDto> filterValidator,
        IValidator<TemplateLanguageCoverageFindingFilterDto> findingFilterValidator,
        IValidator<TemplateLanguageCoverageAcknowledgeInputDto> ackValidator)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(filterValidator);
        ArgumentNullException.ThrowIfNull(findingFilterValidator);
        ArgumentNullException.ThrowIfNull(ackValidator);
        _db = db;
        _audit = audit;
        _sqids = sqids;
        _clock = clock;
        _caller = caller;
        _filterValidator = filterValidator;
        _findingFilterValidator = findingFilterValidator;
        _ackValidator = ackValidator;
    }

    /// <inheritdoc />
    public async Task<Result<TemplateLanguageCoverageReportDto>> ComputeCoverageAsync(
        TemplateLanguageCoverageFilterDto filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var validation = await _filterValidator.ValidateAsync(filter, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<TemplateLanguageCoverageReportDto>.Failure(
                ErrorCodes.ValidationFailed, validation.ToString());
        }

        var report = await ComputeCoverageInternalAsync(filter, cancellationToken).ConfigureAwait(false);
        return Result<TemplateLanguageCoverageReportDto>.Success(report);
    }

    /// <inheritdoc />
    public async Task<Result<TemplateLanguageCoverageReportDto>> RecordCoverageRunAsync(
        TemplateLanguageCoverageFilterDto filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var validation = await _filterValidator.ValidateAsync(filter, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<TemplateLanguageCoverageReportDto>.Failure(
                ErrorCodes.ValidationFailed, validation.ToString());
        }

        // 1. Pure projection — compute the gaps independent of any persistence.
        var report = await ComputeCoverageInternalAsync(filter, cancellationToken).ConfigureAwait(false);

        // 2. Reload the gap set unpaged so we persist EVERY finding, not just
        //    those visible on the requested page. The wire report still
        //    honours Skip/Take for the caller.
        var allGapsReport = await ComputeCoverageInternalAsync(
            filter with { Skip = 0, Take = int.MaxValue },
            cancellationToken).ConfigureAwait(false);

        // 3. Pre-load existing OPEN findings to dedupe against the active scan.
        //    Building a HashSet of (templateId, language) keeps the inner loop O(1).
        var templateIds = allGapsReport.Gaps
            .Select(g => DecodeTemplateIdOrThrow(g.TemplateSqid))
            .Distinct()
            .ToList();

        var existingOpen = await _db.TemplateLanguageCoverageFindings
            .Where(f => templateIds.Contains(f.TemplateId)
                && !f.Acknowledged
                && f.IsActive)
            .Select(f => new { f.TemplateId, f.MissingLanguage })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var existingKeys = new HashSet<(long TemplateId, string Language)>(
            existingOpen.Select(e => (e.TemplateId, e.MissingLanguage)));

        // 4. Insert one finding per (template, missingLanguage) gap that does
        //    NOT have an open row already. Emit Critical audit + per-language
        //    counter on each insert.
        var now = _clock.UtcNow;
        var actor = _caller.UserSqid ?? "system";
        var newFindings = new List<TemplateLanguageCoverageFinding>();
        foreach (var gap in allGapsReport.Gaps)
        {
            var templateId = DecodeTemplateIdOrThrow(gap.TemplateSqid);
            foreach (var language in gap.MissingLanguages)
            {
                var key = (templateId, language);
                if (existingKeys.Contains(key))
                {
                    continue;
                }
                existingKeys.Add(key);

                var row = new TemplateLanguageCoverageFinding
                {
                    TemplateId = templateId,
                    MissingLanguage = language,
                    DetectedAt = now,
                    Acknowledged = false,
                    CreatedAtUtc = now,
                    CreatedBy = actor,
                    IsActive = true,
                };
                _db.TemplateLanguageCoverageFindings.Add(row);
                newFindings.Add(row);
            }
        }

        if (newFindings.Count > 0)
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            foreach (var f in newFindings)
            {
                CnasMeter.TemplateLanguageCoverageGapDetected.Add(
                    1, new KeyValuePair<string, object?>("language", f.MissingLanguage));

                var templateCode = await _db.DocumentTemplates
                    .AsNoTracking()
                    .Where(t => t.Id == f.TemplateId)
                    .Select(t => t.Code)
                    .FirstOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false);

                // Audit payload references only operational metadata — NO PII.
                var details = JsonSerializer.Serialize(new
                {
                    templateId = f.TemplateId,
                    templateCode = templateCode ?? string.Empty,
                    missingLanguage = f.MissingLanguage,
                });
                await _audit.RecordAsync(
                    AuditGapDetected,
                    AuditSeverity.Critical,
                    actor,
                    nameof(TemplateLanguageCoverageFinding),
                    f.Id,
                    details,
                    _caller.SourceIp,
                    _caller.CorrelationId,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        CnasMeter.TemplateLanguageCoverageRunCompleted.Add(
            1,
            new KeyValuePair<string, object?>(
                "trigger_kind",
                _caller.UserSqid is null ? "scheduled" : "manual"));

        return Result<TemplateLanguageCoverageReportDto>.Success(report);
    }

    /// <inheritdoc />
    public async Task<Result<TemplateLanguageCoverageFindingDto>> AcknowledgeFindingAsync(
        string findingSqid,
        TemplateLanguageCoverageAcknowledgeInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var validation = await _ackValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<TemplateLanguageCoverageFindingDto>.Failure(
                ErrorCodes.ValidationFailed, validation.ToString());
        }

        var decoded = _sqids.TryDecode(findingSqid);
        if (decoded.IsFailure)
        {
            return Result<TemplateLanguageCoverageFindingDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var row = await _db.TemplateLanguageCoverageFindings
            .FirstOrDefaultAsync(f => f.Id == decoded.Value && f.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            return Result<TemplateLanguageCoverageFindingDto>.Failure(
                ErrorCodes.NotFound, "Coverage finding not found.");
        }
        if (row.Acknowledged)
        {
            return Result<TemplateLanguageCoverageFindingDto>.Failure(
                ErrorCodes.Conflict, "Finding is already acknowledged.");
        }

        var now = _clock.UtcNow;
        row.Acknowledged = true;
        row.AcknowledgedAt = now;
        row.AcknowledgedByUserId = _caller.UserId;
        row.AcknowledgementNote = input.Note;
        row.UpdatedAtUtc = now;
        row.UpdatedBy = _caller.UserSqid ?? "admin";
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        CnasMeter.TemplateLanguageCoverageGapAcknowledged.Add(1);

        var templateCode = await _db.DocumentTemplates
            .AsNoTracking()
            .Where(t => t.Id == row.TemplateId)
            .Select(t => t.Code)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        var details = JsonSerializer.Serialize(new
        {
            findingId = row.Id,
            templateId = row.TemplateId,
            templateCode = templateCode ?? string.Empty,
            missingLanguage = row.MissingLanguage,
        });
        await _audit.RecordAsync(
            AuditGapAcknowledged,
            AuditSeverity.Critical,
            _caller.UserSqid ?? "admin",
            nameof(TemplateLanguageCoverageFinding),
            row.Id,
            details,
            _caller.SourceIp,
            _caller.CorrelationId,
            cancellationToken).ConfigureAwait(false);

        return Result<TemplateLanguageCoverageFindingDto>.Success(await ToDtoAsync(row, cancellationToken).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public async Task<Result<TemplateLanguageCoverageFindingPageDto>> ListFindingsAsync(
        TemplateLanguageCoverageFindingFilterDto filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var validation = await _findingFilterValidator.ValidateAsync(filter, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<TemplateLanguageCoverageFindingPageDto>.Failure(
                ErrorCodes.ValidationFailed, validation.ToString());
        }

        IQueryable<TemplateLanguageCoverageFinding> query = _db.TemplateLanguageCoverageFindings
            .AsNoTracking()
            .Where(f => f.IsActive);

        if (filter.Acknowledged is { } ackFlag)
        {
            query = query.Where(f => f.Acknowledged == ackFlag);
        }
        if (!string.IsNullOrWhiteSpace(filter.MissingLanguage))
        {
            var lang = filter.MissingLanguage;
            query = query.Where(f => f.MissingLanguage == lang);
        }

        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        var rows = await query
            .OrderByDescending(f => f.DetectedAt)
            .ThenByDescending(f => f.Id)
            .Skip(filter.Skip)
            .Take(filter.Take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Pre-fetch the parent template codes in one round-trip — avoids the
        // N+1 pattern of probing for each finding row's code individually.
        var templateIds = rows.Select(r => r.TemplateId).Distinct().ToList();
        var templateCodes = await _db.DocumentTemplates
            .AsNoTracking()
            .Where(t => templateIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Code })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var codeLookup = templateCodes.ToDictionary(x => x.Id, x => x.Code);

        var items = rows.Select(r => ToDto(r, codeLookup.GetValueOrDefault(r.TemplateId) ?? string.Empty))
                        .ToList();
        var page = new TemplateLanguageCoverageFindingPageDto(
            Items: items,
            Total: total,
            Skip: filter.Skip,
            Take: filter.Take);
        return Result<TemplateLanguageCoverageFindingPageDto>.Success(page);
    }

    /// <summary>
    /// Shared coverage projection. Pure read; no audit / persistence side
    /// effects. The required-language set defaults to RO/EN/RU when the
    /// supplied filter leaves <c>RequiredLanguages</c> null / empty.
    /// </summary>
    /// <param name="filter">Filter envelope.</param>
    /// <param name="cancellationToken">Cancellation propagated from the caller.</param>
    /// <returns>The fully-populated report DTO.</returns>
    private async Task<TemplateLanguageCoverageReportDto> ComputeCoverageInternalAsync(
        TemplateLanguageCoverageFilterDto filter,
        CancellationToken cancellationToken)
    {
        // 1. Resolve required-language set + normalise to lower-case.
        var requiredRaw = filter.RequiredLanguages is { Count: > 0 }
            ? filter.RequiredLanguages
            : CanonicalRequiredLanguages;
        var required = requiredRaw
            .Select(c => (c ?? string.Empty).ToLowerInvariant())
            .Where(c => c.Length > 0)
            .Distinct()
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();

        // 2. Pick templates — IsActive when IncludeRetiredTemplates=false.
        IQueryable<DocumentTemplate> templateQuery = _db.DocumentTemplates;
        if (!filter.IncludeRetiredTemplates)
        {
            templateQuery = templateQuery.Where(t => t.IsActive);
        }

        // 3. Load template + active variants. The dataset is small (≤ a few
        //    hundred templates × 3 variants in practice) so a single
        //    materialisation is fine.
        var templates = await templateQuery
            .OrderBy(t => t.Code)
            .Select(t => new
            {
                t.Id,
                t.Code,
                t.Name,
                t.DefaultLanguage,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var templateIds = templates.Select(t => t.Id).ToList();
        var variantRows = await _db.TemplateVariants
            .AsNoTracking()
            .Where(v => templateIds.Contains(v.TemplateId) && v.IsActive)
            .Select(v => new
            {
                v.TemplateId,
                v.Language,
                v.IsApproved,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var variantsByTemplate = variantRows
            .GroupBy(v => v.TemplateId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(v => (Language: v.Language, IsApproved: v.IsApproved)).ToList());

        // 4. Build per-template gap rows.
        var allGaps = new List<TemplateLanguageCoverageGapDto>();
        var fullyCovered = 0;
        foreach (var template in templates)
        {
            var variants = variantsByTemplate.TryGetValue(template.Id, out var list)
                ? list
                : new List<(string Language, bool IsApproved)>();

            var approvedSet = new HashSet<string>(
                variants.Where(v => v.IsApproved).Select(v => v.Language),
                StringComparer.Ordinal);
            var unapprovedSet = new HashSet<string>(
                variants.Where(v => !v.IsApproved).Select(v => v.Language),
                StringComparer.Ordinal);

            HashSet<string> coveredSet = filter.OnlyApproved
                ? approvedSet
                : new HashSet<string>(approvedSet.Union(unapprovedSet), StringComparer.Ordinal);

            var missing = required.Where(r => !coveredSet.Contains(r))
                                  .OrderBy(c => c, StringComparer.Ordinal)
                                  .ToList();
            if (missing.Count == 0)
            {
                fullyCovered++;
                continue;
            }

            allGaps.Add(new TemplateLanguageCoverageGapDto(
                TemplateSqid: _sqids.Encode(template.Id),
                TemplateCode: template.Code,
                TemplateNameDefault: template.Name,
                DefaultLanguage: template.DefaultLanguage,
                MissingLanguages: missing,
                ExistingApprovedLanguages: approvedSet
                    .OrderBy(c => c, StringComparer.Ordinal).ToList(),
                ExistingUnapprovedLanguages: unapprovedSet
                    .OrderBy(c => c, StringComparer.Ordinal).ToList()));
        }

        // 5. Sort + page the gap list.
        var sortedGaps = allGaps.OrderBy(g => g.TemplateCode, StringComparer.Ordinal).ToList();
        var pageSize = Math.Max(0, filter.Take);
        var skip = Math.Max(0, filter.Skip);
        var paged = sortedGaps.Skip(skip).Take(pageSize == 0 ? 0 : pageSize).ToList();

        return new TemplateLanguageCoverageReportDto(
            TotalTemplatesScanned: templates.Count,
            TotalTemplatesFullyCovered: fullyCovered,
            TotalTemplatesWithGaps: sortedGaps.Count,
            RequiredLanguages: required,
            Gaps: paged,
            Total: sortedGaps.Count,
            Skip: skip,
            Take: filter.Take,
            ComputedAtUtc: _clock.UtcNow);
    }

    /// <summary>
    /// Decodes a Sqid back to a template id, raising
    /// <see cref="InvalidOperationException"/> when the decode fails. The
    /// coverage projection has just produced these Sqids itself, so a
    /// failure here is a coding bug, not user input.
    /// </summary>
    /// <param name="sqid">Sqid-encoded template id.</param>
    /// <returns>The decoded long id.</returns>
    /// <exception cref="InvalidOperationException">If decode fails.</exception>
    private long DecodeTemplateIdOrThrow(string sqid)
    {
        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            throw new InvalidOperationException(
                $"Internal sqid '{sqid}' failed to round-trip: {decoded.ErrorMessage}.");
        }
        return decoded.Value;
    }

    /// <summary>
    /// Projects a persisted finding row into its outbound DTO. Resolves the
    /// parent template code with a single additional round-trip — used only
    /// on the acknowledgement happy path (one row).
    /// </summary>
    /// <param name="row">Persisted finding.</param>
    /// <param name="cancellationToken">Cancellation propagated from the caller.</param>
    /// <returns>The wire DTO.</returns>
    private async Task<TemplateLanguageCoverageFindingDto> ToDtoAsync(
        TemplateLanguageCoverageFinding row,
        CancellationToken cancellationToken)
    {
        var templateCode = await _db.DocumentTemplates
            .AsNoTracking()
            .Where(t => t.Id == row.TemplateId)
            .Select(t => t.Code)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return ToDto(row, templateCode ?? string.Empty);
    }

    /// <summary>
    /// Projects a persisted finding row into its outbound DTO. Used by the
    /// list endpoint where the parent template code has already been
    /// pre-fetched in one round-trip.
    /// </summary>
    /// <param name="row">Persisted finding.</param>
    /// <param name="templateCode">Pre-fetched parent template code.</param>
    /// <returns>The wire DTO.</returns>
    private TemplateLanguageCoverageFindingDto ToDto(
        TemplateLanguageCoverageFinding row,
        string templateCode)
    {
        return new TemplateLanguageCoverageFindingDto(
            Id: _sqids.Encode(row.Id),
            TemplateSqid: _sqids.Encode(row.TemplateId),
            TemplateCode: templateCode,
            MissingLanguage: row.MissingLanguage,
            DetectedAt: row.DetectedAt,
            Acknowledged: row.Acknowledged,
            AcknowledgedAt: row.AcknowledgedAt,
            AcknowledgedByUserSqid: row.AcknowledgedByUserId is { } uid ? _sqids.Encode(uid) : null,
            AcknowledgementNote: row.AcknowledgementNote);
    }
}
