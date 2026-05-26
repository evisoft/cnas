using System.Globalization;
using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Templates;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cnas.Ps.Infrastructure.Services.Templates;

/// <summary>
/// R0132 / CF 17.18 — default <see cref="ITemplateVersionHistoryService"/> implementation
/// backed by <see cref="ICnasDbContext"/>. Provides paged listing, structured diff, and
/// rollback over historical <see cref="DocumentTemplate"/> rows.
/// </summary>
/// <remarks>
/// <para>
/// <b>Rollback semantics.</b> A rollback INSERTS a new row that copies the binary
/// coordinates + metadata from the target older row, then marks the new row as current
/// and demotes the previously-current row. The target row is never mutated, so the
/// historical record is preserved verbatim.
/// </para>
/// <para>
/// <b>Diff scope.</b> The diff compares METADATA fields (name, description,
/// content-sha256, content-length, content-type, language). The DOCX binary itself is
/// NOT inlined into the diff — those bodies can be tens of megabytes and the diff is a
/// human-review aid, not a forensic byte-comparison.
/// </para>
/// </remarks>
/// <param name="db">Per-request EF Core context.</param>
/// <param name="sqids">Sqid encoder/decoder.</param>
/// <param name="clock">Time provider; no <c>DateTime.UtcNow</c>.</param>
/// <param name="caller">Active caller context.</param>
/// <param name="audit">Centralised audit-writer facade.</param>
/// <param name="rollbackValidator">Validator for the rollback input.</param>
/// <param name="logger">Structured logger.</param>
public sealed class TemplateVersionHistoryService(
    ICnasDbContext db,
    ISqidService sqids,
    ICnasTimeProvider clock,
    ICallerContext caller,
    IAuditService audit,
    FluentValidation.IValidator<TemplateRollbackInputDto> rollbackValidator,
    ILogger<TemplateVersionHistoryService> logger)
    : ITemplateVersionHistoryService
{
    private readonly ICnasDbContext _db = db;
    private readonly ISqidService _sqids = sqids;
    private readonly ICnasTimeProvider _clock = clock;
    private readonly ICallerContext _caller = caller;
    private readonly IAuditService _audit = audit;
    private readonly FluentValidation.IValidator<TemplateRollbackInputDto> _rollbackValidator = rollbackValidator;
    private readonly ILogger<TemplateVersionHistoryService> _logger = logger;

    /// <summary>Stable audit-event code for rollback operations.</summary>
    private const string RollbackAuditEventCode = "TEMPLATE.VERSION_ROLLBACK";

    /// <inheritdoc />
    public async System.Threading.Tasks.Task<Result<TemplateVersionPageDto>> ListVersionsAsync(
        string templateCode,
        int skip,
        int take,
        System.Threading.CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(templateCode))
        {
            return Result<TemplateVersionPageDto>.Failure(
                ErrorCodes.ValidationFailed, "Template code is required.");
        }
        if (skip < 0)
        {
            return Result<TemplateVersionPageDto>.Failure(
                ErrorCodes.ValidationFailed, "Skip must be ≥ 0.");
        }
        if (take is < 1 or > 200)
        {
            return Result<TemplateVersionPageDto>.Failure(
                ErrorCodes.ValidationFailed, "Take must be 1..200.");
        }

        // Canonicalise to match DocumentTemplate's lower-case storage convention.
        var code = templateCode.Trim().ToLowerInvariant();

        var baseQuery = _db.DocumentTemplates.Where(t => t.Code == code && t.IsActive);
        var total = await baseQuery.LongCountAsync(cancellationToken).ConfigureAwait(false);
        if (total == 0)
        {
            return Result<TemplateVersionPageDto>.Failure(
                ErrorCodes.NotFound, $"No template version exists for code '{code}'.");
        }

        var rows = await baseQuery
            .OrderByDescending(t => t.Version)
            .Skip(skip).Take(take)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        IReadOnlyList<DocumentTemplateDto> items = rows.Select(ToDto).ToList();
        return Result<TemplateVersionPageDto>.Success(new TemplateVersionPageDto(code, items, total));
    }

    /// <inheritdoc />
    public async System.Threading.Tasks.Task<Result<TemplateVersionDiffDto>> DiffAsync(
        string baselineVersionSqid,
        string currentVersionSqid,
        System.Threading.CancellationToken cancellationToken = default)
    {
        var baselineDecoded = _sqids.TryDecode(baselineVersionSqid);
        if (baselineDecoded.IsFailure)
        {
            return Result<TemplateVersionDiffDto>.Failure(
                baselineDecoded.ErrorCode!, baselineDecoded.ErrorMessage!);
        }
        var currentDecoded = _sqids.TryDecode(currentVersionSqid);
        if (currentDecoded.IsFailure)
        {
            return Result<TemplateVersionDiffDto>.Failure(
                currentDecoded.ErrorCode!, currentDecoded.ErrorMessage!);
        }

        var baseline = await _db.DocumentTemplates
            .SingleOrDefaultAsync(t => t.Id == baselineDecoded.Value, cancellationToken)
            .ConfigureAwait(false);
        if (baseline is null)
        {
            return Result<TemplateVersionDiffDto>.Failure(
                ErrorCodes.NotFound, "Baseline template version not found.");
        }
        var current = await _db.DocumentTemplates
            .SingleOrDefaultAsync(t => t.Id == currentDecoded.Value, cancellationToken)
            .ConfigureAwait(false);
        if (current is null)
        {
            return Result<TemplateVersionDiffDto>.Failure(
                ErrorCodes.NotFound, "Current template version not found.");
        }

        if (!string.Equals(baseline.Code, current.Code, StringComparison.Ordinal))
        {
            return Result<TemplateVersionDiffDto>.Failure(
                ErrorCodes.TemplateVersionMismatch,
                $"Baseline (code={baseline.Code}) and current (code={current.Code}) belong to different templates.");
        }

        var entries = new List<TemplateVersionDiffEntryDto>();
        AddEntry(entries, nameof(DocumentTemplate.Name), baseline.Name, current.Name);
        AddEntry(entries, nameof(DocumentTemplate.Description), baseline.Description, current.Description);
        AddEntry(entries, nameof(DocumentTemplate.ContentSha256), baseline.ContentSha256, current.ContentSha256);
        AddEntry(entries, nameof(DocumentTemplate.ContentLength),
            baseline.ContentLength.ToString(CultureInfo.InvariantCulture),
            current.ContentLength.ToString(CultureInfo.InvariantCulture));
        AddEntry(entries, nameof(DocumentTemplate.ContentType), baseline.ContentType, current.ContentType);
        AddEntry(entries, nameof(DocumentTemplate.DefaultLanguage), baseline.DefaultLanguage, current.DefaultLanguage);

        return Result<TemplateVersionDiffDto>.Success(new TemplateVersionDiffDto(
            BaselineVersion: baseline.Version,
            CurrentVersion: current.Version,
            Code: baseline.Code,
            Entries: entries));
    }

    /// <inheritdoc />
    public async System.Threading.Tasks.Task<Result<DocumentTemplateDto>> RollbackToAsync(
        string targetVersionSqid,
        TemplateRollbackInputDto input,
        System.Threading.CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var validation = await _rollbackValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<DocumentTemplateDto>.Failure(
                ErrorCodes.ValidationFailed, validation.ToString("; "));
        }

        var decoded = _sqids.TryDecode(targetVersionSqid);
        if (decoded.IsFailure)
        {
            return Result<DocumentTemplateDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        var target = await _db.DocumentTemplates
            .SingleOrDefaultAsync(t => t.Id == decoded.Value, cancellationToken)
            .ConfigureAwait(false);
        if (target is null)
        {
            return Result<DocumentTemplateDto>.Failure(
                ErrorCodes.NotFound, "Target template version not found.");
        }

        // Find the current row for the same code (if any) to demote it.
        var currentRow = await _db.DocumentTemplates
            .SingleOrDefaultAsync(t => t.Code == target.Code && t.IsCurrent && t.IsActive, cancellationToken)
            .ConfigureAwait(false);

        // Compute the new version number = max(version) + 1 for this code.
        var maxVersion = await _db.DocumentTemplates
            .Where(t => t.Code == target.Code)
            .Select(t => (int?)t.Version)
            .MaxAsync(cancellationToken).ConfigureAwait(false) ?? 0;
        var newVersion = maxVersion + 1;

        var now = _clock.UtcNow;

        // Mint a NEW row that copies the target's payload coordinates. Storage key
        // includes the new version so the blob namespace is still per-version.
        var fresh = new DocumentTemplate
        {
            Code = target.Code,
            Name = target.Name,
            Description = target.Description,
            Version = newVersion,
            IsCurrent = true,
            StorageObjectKey = $"templates/{target.Code}/v{newVersion}/{target.Code}.docx",
            ContentType = target.ContentType,
            ContentLength = target.ContentLength,
            ContentSha256 = target.ContentSha256,
            DefaultLanguage = target.DefaultLanguage,
            CreatedAtUtc = now,
            CreatedBy = _caller.UserSqid ?? "system",
            IsActive = true,
        };

        if (currentRow is not null && currentRow.Id != target.Id)
        {
            currentRow.IsCurrent = false;
            currentRow.UpdatedAtUtc = now;
            currentRow.UpdatedBy = _caller.UserSqid ?? "system";
        }

        _db.DocumentTemplates.Add(fresh);
        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogWarning(ex, "Failed to persist template rollback for code {Code}.", target.Code);
            return Result<DocumentTemplateDto>.Failure(
                ErrorCodes.Internal, "Could not persist the template-version rollback.");
        }

        CnasMeter.TemplateVersionRollback.Add(1,
            new KeyValuePair<string, object?>("template_code", target.Code));

        var details = JsonSerializer.Serialize(new
        {
            code = target.Code,
            fromVersion = currentRow?.Version,
            toVersion = newVersion,
            sourceVersion = target.Version,
            reason = input.Reason,
        });

        await _audit.RecordAsync(
            eventCode: RollbackAuditEventCode,
            severity: AuditSeverity.Critical,
            actorId: _caller.UserSqid ?? "system",
            targetEntity: nameof(DocumentTemplate),
            targetEntityId: fresh.Id,
            detailsJson: details,
            sourceIp: _caller.SourceIp,
            correlationId: _caller.CorrelationId,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return Result<DocumentTemplateDto>.Success(ToDto(fresh));
    }

    /// <summary>
    /// Projects a <see cref="DocumentTemplate"/> entity into the boundary DTO with a
    /// Sqid-encoded id (CLAUDE.md RULE 3).
    /// </summary>
    /// <param name="t">Entity to project.</param>
    /// <returns>The DTO.</returns>
    private DocumentTemplateDto ToDto(DocumentTemplate t) => new(
        Id: _sqids.Encode(t.Id),
        Code: t.Code,
        Name: t.Name,
        Description: t.Description,
        Version: t.Version,
        IsCurrent: t.IsCurrent,
        ContentSha256: t.ContentSha256,
        ContentLength: t.ContentLength,
        CreatedAtUtc: t.CreatedAtUtc);

    /// <summary>
    /// Adds a diff entry to <paramref name="entries"/> when the two stringified values
    /// differ. No-op when both values are identical.
    /// </summary>
    /// <param name="entries">Mutable list of diff entries.</param>
    /// <param name="fieldPath">Stable dotted field path.</param>
    /// <param name="baseline">Stringified baseline value.</param>
    /// <param name="current">Stringified current value.</param>
    private static void AddEntry(
        List<TemplateVersionDiffEntryDto> entries,
        string fieldPath,
        string? baseline,
        string? current)
    {
        if (string.Equals(baseline, current, StringComparison.Ordinal))
        {
            return;
        }
        var kind =
            baseline is null ? "Added" :
            current is null ? "Removed" :
            "Modified";
        entries.Add(new TemplateVersionDiffEntryDto(fieldPath, kind, baseline, current));
    }
}
