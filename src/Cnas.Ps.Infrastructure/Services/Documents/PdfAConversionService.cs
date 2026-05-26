using Cnas.Ps.Application.Documents;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Observability;
using FluentValidation;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.Services.Documents;

/// <summary>
/// R0341 / TOR CF 11.06 — placeholder implementation of
/// <see cref="IPdfAConversionService"/>. Returns
/// <see cref="IPdfAConversionService.EngineNotAvailableCode"/> as a
/// deterministic <see cref="Result{T}"/> failure when no engine is configured.
/// Future iterations swap the body for a real conversion library
/// (PdfPig / iText / commercial) without changing callers.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a placeholder.</b> Production-grade PDF/A conversion is a
/// license-gated dependency; until an engine is approved we ship the
/// contract + deterministic failure so the wider system can wire calls
/// against a stable interface. Operators see the failure on the audit log
/// rather than on a thrown exception.
/// </para>
/// </remarks>
public sealed class PdfAConversionService : IPdfAConversionService
{
    private readonly IOptions<PdfAConversionOptions> _options;
    private readonly IValidator<PdfAConversionInputDto> _validator;

    /// <summary>Constructs the placeholder service.</summary>
    /// <param name="options">Bound <see cref="PdfAConversionOptions"/> envelope.</param>
    /// <param name="validator">FluentValidation envelope validator.</param>
    public PdfAConversionService(
        IOptions<PdfAConversionOptions> options,
        IValidator<PdfAConversionInputDto> validator)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(validator);
        _options = options;
        _validator = validator;
    }

    /// <inheritdoc />
    public async Task<Result<PdfAConversionOutcomeDto>> ConvertAsync(
        PdfAConversionInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var v = await _validator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            CnasMeter.PdfAConversionAttempted.Add(
                1, new KeyValuePair<string, object?>("outcome", "failure"));
            return Result<PdfAConversionOutcomeDto>.Failure(
                ErrorCodes.ValidationFailed, v.Errors[0].ErrorMessage);
        }

        var engine = _options.Value.Engine;
        if (string.IsNullOrWhiteSpace(engine))
        {
            CnasMeter.PdfAConversionAttempted.Add(
                1, new KeyValuePair<string, object?>("outcome", "engine_not_available"));
            return Result<PdfAConversionOutcomeDto>.Failure(
                IPdfAConversionService.EngineNotAvailableCode,
                "Cnas:Documents:PdfA:Engine is not configured.");
        }

        // Concrete engine wiring lands when a license-cleared library is
        // approved. Keep the failure explicit so operators see the seam.
        CnasMeter.PdfAConversionAttempted.Add(
            1, new KeyValuePair<string, object?>("outcome", "engine_not_available"));
        return Result<PdfAConversionOutcomeDto>.Failure(
            IPdfAConversionService.EngineNotAvailableCode,
            $"PDF/A conversion engine '{engine}' is recognised but not wired in this build (placeholder).");
    }
}

/// <summary>
/// R0341 / TOR CF 11.06 — bound options envelope for the placeholder PDF/A
/// service. Empty by default; operators flip <see cref="Engine"/> once a
/// concrete library is approved.
/// </summary>
public sealed class PdfAConversionOptions
{
    /// <summary>Well-known configuration section name (<c>Cnas:Documents:PdfA</c>).</summary>
    public const string SectionName = "Cnas:Documents:PdfA";

    /// <summary>
    /// Stable identifier of the configured engine. Blank by default — when
    /// blank the placeholder returns <c>PDFA.ENGINE_NOT_AVAILABLE</c> so
    /// production deployments must opt in explicitly. Values are free-form
    /// strings (e.g. <c>PdfPig</c>, <c>iText</c>) — the service consults this
    /// solely to surface a meaningful diagnostic.
    /// </summary>
    public string Engine { get; set; } = string.Empty;
}
