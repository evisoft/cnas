using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Calculations;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// R0143 / CF 17.19 — default implementation of
/// <see cref="IServicePassportConfigMatrixService"/>. Assembles the eight-column
/// configuration matrix by joining the addressed
/// <see cref="Cnas.Ps.Core.Domain.ServicePassport"/> current revision with conventional
/// per-service template codes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Template code convention.</b> The Annex 7 templates have stable canonical codes
/// (see <c>Cnas.Ps.Infrastructure.Documents.Templates.DeciziaPensieTemplate.Code</c>
/// and friends) shared across every service passport. We surface those codes verbatim
/// — per-passport overrides land alongside future
/// <c>ServicePassport.{Receipt,Decision,...}TemplateCodeOverride</c> columns.
/// </para>
/// <para>
/// <b>Tolerant JSON parsing.</b> Malformed
/// <c>MandatoryAttachmentsJson</c> / <c>CalcFormulasJson</c> rows surface as empty
/// collections rather than failing the matrix call so a single bad passport cannot
/// degrade the catalogue endpoint.
/// </para>
/// </remarks>
public sealed class ServicePassportConfigMatrixService : IServicePassportConfigMatrixService
{
    /// <summary>Tolerant JSON-parse options.</summary>
    private static readonly JsonDocumentOptions DocOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// Canonical Annex 7 template codes used by the matrix when no per-passport override
    /// exists. Each constant mirrors the value carried by the corresponding
    /// <c>IDocxTemplate</c>'s <c>Code</c> property.
    /// </summary>
    private const string DefaultReceiptTemplateCode = "recipisa";
    private const string DefaultDecisionTemplateCode = "decizia-pensie";
    private const string DefaultFisaCalculTemplateCode = "fisa-de-calcul";
    private const string DefaultPrintFormTemplateCode = "cerere";

    private readonly ICnasDbContext _db;
    private readonly ISqidService _sqids;
    // The evaluator is captured so callers / future internal calls can compose against
    // the same instance the matrix surface advertises; it has no current call-site
    // inside this service yet (the formulas are returned verbatim and the caller
    // evaluates them downstream — see the test for the canonical pattern).
#pragma warning disable IDE0052 // intentional forward-looking capture
    private readonly IExpressionEvaluator _evaluator;
#pragma warning restore IDE0052

    /// <summary>
    /// Wires the service with its collaborators.
    /// </summary>
    /// <param name="db">EF Core context abstraction (current passport revision lookup).</param>
    /// <param name="sqids">Sqid encoder used to populate <c>ServicePassportConfigMatrixDto.Id</c>.</param>
    /// <param name="evaluator">Expression evaluator advertised alongside the matrix (forward-looking).</param>
    /// <exception cref="ArgumentNullException">Any parameter is <see langword="null"/>.</exception>
    public ServicePassportConfigMatrixService(
        ICnasDbContext db,
        ISqidService sqids,
        IExpressionEvaluator evaluator)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(evaluator);
        _db = db;
        _sqids = sqids;
        _evaluator = evaluator;
    }

    /// <inheritdoc />
    public async Task<Result<ServicePassportConfigMatrixDto>> GetMatrixAsync(
        string passportCode,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(passportCode))
        {
            return Result<ServicePassportConfigMatrixDto>.Failure(
                ErrorCodes.NotFound,
                "Passport code is required.");
        }

        var code = passportCode.Trim();
        var passport = await _db.ServicePassports
            .Where(p => p.IsActive && p.IsCurrent && p.Code == code)
            .Select(p => new
            {
                p.Id,
                p.Code,
                p.Version,
                p.FormSchemaJson,
                p.DecisionRulesJson,
                p.MandatoryAttachmentsJson,
                p.CalcFormulasJson,
            })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (passport is null)
        {
            return Result<ServicePassportConfigMatrixDto>.Failure(
                ErrorCodes.NotFound,
                $"No active current service passport for code '{code}'.");
        }

        var mandatory = ParseMandatoryAttachments(passport.MandatoryAttachmentsJson);
        var calcFormulas = ParseCalcFormulas(passport.CalcFormulasJson);

        // Validation rules for the matrix are aggregated from the per-template metadata
        // (the addressed decision-template's ValidationRulesJson). We surface the raw
        // string verbatim so the admin UI can present it without re-serialising; null
        // when no template-level rules exist.
        string? validationRulesJson = await _db.DocumentTemplates
            .Where(t => t.IsActive && t.IsCurrent && t.Code == DefaultDecisionTemplateCode)
            .Select(t => t.ValidationRulesJson)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        var dto = new ServicePassportConfigMatrixDto(
            Id: _sqids.Encode(passport.Id),
            Code: passport.Code,
            Version: passport.Version,
            FormSchemaJson: passport.FormSchemaJson,
            ValidationRulesJson: validationRulesJson,
            MandatoryAttachments: mandatory,
            ReceiptTemplateCode: DefaultReceiptTemplateCode,
            DecisionTemplateCode: DefaultDecisionTemplateCode,
            FisaCalculTemplateCode: DefaultFisaCalculTemplateCode,
            CalcFormulas: calcFormulas,
            ProcessingRulesJson: passport.DecisionRulesJson,
            PrintFormTemplateCode: DefaultPrintFormTemplateCode);

        return Result<ServicePassportConfigMatrixDto>.Success(dto);
    }

    /// <summary>
    /// Parses the JSON array of mandatory-attachment descriptors. Malformed or absent
    /// JSON surfaces as the empty list (defensive — a single bad row must not break
    /// the matrix call).
    /// </summary>
    /// <param name="json">The persisted JSON array; may be <see langword="null"/>.</param>
    /// <returns>Parsed attachments, in array order.</returns>
    private static IReadOnlyList<ServicePassportMandatoryAttachmentDto> ParseMandatoryAttachments(string? json)
    {
        var list = new List<ServicePassportMandatoryAttachmentDto>();
        if (string.IsNullOrWhiteSpace(json)) return list;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, DocOptions);
        }
        catch (JsonException)
        {
            return list;
        }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array) return list;
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object) continue;
                if (!element.TryGetProperty("documentTypeCode", out var codeEl)
                    || codeEl.ValueKind != JsonValueKind.String) continue;
                var docCode = codeEl.GetString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(docCode)) continue;

                var min = element.TryGetProperty("cardinalityMin", out var minEl)
                    && minEl.ValueKind == JsonValueKind.Number
                    && minEl.TryGetInt32(out var mi)
                    ? mi : 1;
                var max = element.TryGetProperty("cardinalityMax", out var maxEl)
                    && maxEl.ValueKind == JsonValueKind.Number
                    && maxEl.TryGetInt32(out var ma)
                    ? ma : int.MaxValue;
                list.Add(new ServicePassportMandatoryAttachmentDto(docCode, min, max));
            }
        }
        return list;
    }

    /// <summary>
    /// Parses the JSON array of calc-formula rows.
    /// </summary>
    /// <param name="json">The persisted JSON array; may be <see langword="null"/>.</param>
    /// <returns>Parsed formulas, in array order.</returns>
    private static IReadOnlyList<ServicePassportCalcFormulaDto> ParseCalcFormulas(string? json)
    {
        var list = new List<ServicePassportCalcFormulaDto>();
        if (string.IsNullOrWhiteSpace(json)) return list;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, DocOptions);
        }
        catch (JsonException)
        {
            return list;
        }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array) return list;
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object) continue;
                if (!element.TryGetProperty("code", out var codeEl)
                    || codeEl.ValueKind != JsonValueKind.String) continue;
                if (!element.TryGetProperty("formula", out var formulaEl)
                    || formulaEl.ValueKind != JsonValueKind.String) continue;
                var code = codeEl.GetString() ?? string.Empty;
                var formula = formulaEl.GetString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(formula)) continue;
                list.Add(new ServicePassportCalcFormulaDto(code, formula));
            }
        }
        return list;
    }
}
