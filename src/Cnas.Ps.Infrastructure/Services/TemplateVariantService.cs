using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Templates;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// R0133 / TOR CF 17.16 — Concrete <see cref="ITemplateVariantService"/>. Persists
/// per-language variants of a <see cref="DocumentTemplate"/>, manages the approval
/// flag with Critical audit emission, and renders preview bodies suitable for
/// listing endpoints.
/// </summary>
/// <remarks>
/// <para>
/// <b>DocxBase64 deep validation.</b> The base-class FluentValidation rule only
/// checks for non-empty / 1..200 / etc.; the magic-byte sniff and 10-MiB cap live
/// here because they need a base64 decode + byte-level inspection. The two checks
/// run in order: (1) decode; (2) verify size ≤ 10 MiB; (3) verify first 4 bytes
/// are the ZIP magic (<c>50 4B 03 04</c>). Failing any returns
/// <see cref="ErrorCodes.FileTypeMismatch"/> or <see cref="ErrorCodes.FileTooLarge"/>
/// — the same stable codes used by the upload-driven <see cref="TemplateAdminService"/>.
/// </para>
/// <para>
/// <b>Approval audit.</b> <see cref="ApproveAsync"/> / <see cref="UnapproveAsync"/>
/// emit Critical audit rows even when the action is a no-op (re-approve an
/// already-approved row). The intent is forensic: an operator who repeatedly
/// touches the approval flag wants to be seen in the audit log on every click.
/// </para>
/// </remarks>
public sealed class TemplateVariantService : ITemplateVariantService
{
    /// <summary>Maximum decoded DOCX blob size — mirrors <see cref="TemplateAdminService.MaxTemplateSize"/> * 2 (10 MiB).</summary>
    public const long MaxDocxSize = 10L * 1024 * 1024;

    /// <summary>ZIP / DOCX magic-byte signature — see <see cref="TemplateAdminService"/>.</summary>
    private static readonly byte[] DocxMagicBytes = [0x50, 0x4B, 0x03, 0x04];

    private readonly ICnasDbContext _db;
    private readonly ICnasTimeProvider _clock;
    private readonly ISqidService _sqids;
    private readonly ICallerContext _caller;
    private readonly IAuditService? _audit;

    /// <summary>
    /// Constructs the service. The <paramref name="audit"/> collaborator is optional
    /// so unit-test harnesses that elect to skip audit wiring don't have to plumb a
    /// no-op double through.
    /// </summary>
    /// <param name="db">EF Core context (scoped).</param>
    /// <param name="clock">UTC clock — never <see cref="DateTime.UtcNow"/> directly.</param>
    /// <param name="sqids">Sqid encode/decode service.</param>
    /// <param name="caller">Current caller context for audit attribution.</param>
    /// <param name="audit">Optional audit-record sink; no-op when null.</param>
    /// <exception cref="ArgumentNullException">When a required collaborator is null.</exception>
    public TemplateVariantService(
        ICnasDbContext db,
        ICnasTimeProvider clock,
        ISqidService sqids,
        ICallerContext caller,
        IAuditService? audit = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(caller);
        _db = db;
        _clock = clock;
        _sqids = sqids;
        _caller = caller;
        _audit = audit;
    }

    /// <inheritdoc />
    public async Task<Result<TemplateVariantOutputDto>> UpsertAsync(
        TemplateVariantUpsertDto dto,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dto);

        // 1. FluentValidation gates the shape.
        var validator = new TemplateVariantUpsertDtoValidator();
        var validation = await validator.ValidateAsync(dto, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<TemplateVariantOutputDto>.Failure(
                ErrorCodes.ValidationFailed,
                string.Join("; ", validation.Errors.Select(e => e.ErrorMessage)));
        }

        // 2. Decode the parent template Sqid (CLAUDE.md RULE 3).
        var decoded = _sqids.TryDecode(dto.TemplateSqid);
        if (decoded.IsFailure)
        {
            return Result<TemplateVariantOutputDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var templateId = decoded.Value;

        // 3. Resolve the parent template — must exist and be active. Look up by Id
        //    only (not by IsCurrent) because the variant rides on the template,
        //    not on a specific version.
        var template = await _db.DocumentTemplates
            .FirstOrDefaultAsync(t => t.Id == templateId && t.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (template is null)
        {
            return Result<TemplateVariantOutputDto>.Failure(
                ErrorCodes.NotFound,
                $"No template found with id '{dto.TemplateSqid}'.");
        }

        // 4. Optionally validate the DOCX blob.
        byte[]? docxBytes = null;
        if (!string.IsNullOrWhiteSpace(dto.DocxBase64))
        {
            byte[] decodedDocx;
            try
            {
                decodedDocx = Convert.FromBase64String(dto.DocxBase64);
            }
            catch (FormatException)
            {
                return Result<TemplateVariantOutputDto>.Failure(
                    ErrorCodes.FileTypeMismatch,
                    "DocxBase64 is not a valid base-64 string.");
            }
            if (decodedDocx.LongLength > MaxDocxSize)
            {
                return Result<TemplateVariantOutputDto>.Failure(
                    ErrorCodes.FileTooLarge,
                    $"DocxBase64 exceeds the {MaxDocxSize}-byte cap.");
            }
            if (decodedDocx.Length < 4
                || !decodedDocx.AsSpan(0, 4).SequenceEqual(DocxMagicBytes))
            {
                return Result<TemplateVariantOutputDto>.Failure(
                    ErrorCodes.FileTypeMismatch,
                    "DocxBase64 does not start with the DOCX magic bytes (50 4B 03 04).");
            }
            docxBytes = decodedDocx;
        }

        // 5. Upsert by (TemplateId, Language).
        var existing = await _db.TemplateVariants
            .FirstOrDefaultAsync(
                v => v.TemplateId == templateId && v.Language == dto.Language && v.IsActive,
                cancellationToken)
            .ConfigureAwait(false);

        TemplateVariant row;
        if (existing is not null)
        {
            existing.SubjectOrTitle = dto.SubjectOrTitle;
            existing.Body = dto.Body;
            existing.TranslatorNote = dto.TranslatorNote;
            if (docxBytes is not null)
            {
                existing.RenderedDocxBytes = docxBytes;
                existing.DocxFileName = $"{template.Code}-{dto.Language}.docx";
            }
            existing.UpdatedAtUtc = _clock.UtcNow;
            existing.UpdatedBy = _caller.UserSqid;
            row = existing;
        }
        else
        {
            row = new TemplateVariant
            {
                TemplateId = templateId,
                Language = dto.Language,
                SubjectOrTitle = dto.SubjectOrTitle,
                Body = dto.Body,
                TranslatorNote = dto.TranslatorNote,
                RenderedDocxBytes = docxBytes,
                DocxFileName = docxBytes is null ? null : $"{template.Code}-{dto.Language}.docx",
                IsApproved = false,
                CreatedAtUtc = _clock.UtcNow,
                CreatedBy = _caller.UserSqid,
                IsActive = true,
            };
            _db.TemplateVariants.Add(row);
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Result<TemplateVariantOutputDto>.Success(ToDto(row, template.Code));
    }

    /// <inheritdoc />
    public async Task<Result> ApproveAsync(long variantId, CancellationToken cancellationToken = default)
        => await FlipApprovalAsync(variantId, true, "TEMPLATE.VARIANT.APPROVED", cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<Result> UnapproveAsync(long variantId, CancellationToken cancellationToken = default)
        => await FlipApprovalAsync(variantId, false, "TEMPLATE.VARIANT.UNAPPROVED", cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<TemplateVariantOutputDto?> GetAsync(
        long templateId,
        string language,
        CancellationToken cancellationToken = default)
    {
        var row = await _db.TemplateVariants
            .AsNoTracking()
            .FirstOrDefaultAsync(
                v => v.TemplateId == templateId && v.Language == language && v.IsActive,
                cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }
        var template = await _db.DocumentTemplates
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == templateId, cancellationToken)
            .ConfigureAwait(false);
        return ToDto(row, template?.Code ?? string.Empty);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TemplateVariantOutputDto>> ListAsync(
        long templateId,
        CancellationToken cancellationToken = default)
    {
        var template = await _db.DocumentTemplates
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == templateId, cancellationToken)
            .ConfigureAwait(false);
        if (template is null)
        {
            return Array.Empty<TemplateVariantOutputDto>();
        }

        var rows = await _db.TemplateVariants
            .AsNoTracking()
            .Where(v => v.TemplateId == templateId && v.IsActive)
            .OrderBy(v => v.Language)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows.Select(r => ToDto(r, template.Code)).ToList();
    }

    /// <summary>
    /// Common implementation for <see cref="ApproveAsync"/> /
    /// <see cref="UnapproveAsync"/>. Loads the row, flips the flag, persists, and
    /// emits the Critical audit event.
    /// </summary>
    /// <param name="variantId">Internal variant id.</param>
    /// <param name="approved">Target value of the flag.</param>
    /// <param name="eventCode">Audit event code (<c>TEMPLATE.VARIANT.APPROVED</c> or <c>...UNAPPROVED</c>).</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<Result> FlipApprovalAsync(long variantId, bool approved, string eventCode, CancellationToken ct)
    {
        var row = await _db.TemplateVariants
            .FirstOrDefaultAsync(v => v.Id == variantId && v.IsActive, ct)
            .ConfigureAwait(false);
        if (row is null)
        {
            return Result.Failure(ErrorCodes.NotFound, $"No template variant found with id {variantId}.");
        }

        row.IsApproved = approved;
        row.UpdatedAtUtc = _clock.UtcNow;
        row.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        if (_audit is not null)
        {
            var template = await _db.DocumentTemplates
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == row.TemplateId, ct)
                .ConfigureAwait(false);
            var details = JsonSerializer.Serialize(new
            {
                templateCode = template?.Code ?? string.Empty,
                language = row.Language,
            });
            await _audit.RecordAsync(
                eventCode: eventCode,
                severity: AuditSeverity.Critical,
                actorId: _caller.UserSqid ?? "system",
                targetEntity: nameof(TemplateVariant),
                targetEntityId: row.Id,
                detailsJson: details,
                sourceIp: _caller.SourceIp,
                correlationId: _caller.CorrelationId,
                cancellationToken: ct).ConfigureAwait(false);
        }

        return Result.Success();
    }

    /// <summary>
    /// Projects a persisted <see cref="TemplateVariant"/> row into its output DTO,
    /// trimming the body to the first 240 chars (the catalog listing only needs a
    /// preview).
    /// </summary>
    /// <param name="row">Persisted variant.</param>
    /// <param name="templateCode">Parent template code (free-text label) — pre-fetched by the caller.</param>
    /// <returns>The DTO ready to ship to the controller layer.</returns>
    private TemplateVariantOutputDto ToDto(TemplateVariant row, string templateCode)
    {
        // The preview is capped at 240 chars so the catalog listing payload stays
        // small. Callers wanting the full body should hit GET .../variants/{lang}
        // which returns the complete record (out of scope for this batch's port).
        var preview = row.Body.Length <= 240 ? row.Body : row.Body[..240];
        _ = templateCode; // Reserved for future enrichment; currently unused in the DTO contract.
        return new TemplateVariantOutputDto(
            Id: _sqids.Encode(row.Id),
            TemplateSqid: _sqids.Encode(row.TemplateId),
            Language: row.Language,
            SubjectOrTitle: row.SubjectOrTitle,
            BodyPreview: preview,
            IsApproved: row.IsApproved,
            TranslatorNote: row.TranslatorNote,
            HasDocx: row.RenderedDocxBytes is not null);
    }
}
