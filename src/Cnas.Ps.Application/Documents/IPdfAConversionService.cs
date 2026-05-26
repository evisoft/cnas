using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Documents;

/// <summary>
/// R0341 / TOR CF 11.06 — converts a raw PDF byte buffer into the canonical
/// long-term-preservation format (PDF/A). Wired in alongside the document
/// generation pipeline so every issued decision / receipt / certificate can
/// be archived in a format that survives the 75-year retention window.
/// </summary>
/// <remarks>
/// <para>
/// <b>Placeholder.</b> The concrete conversion engine (PdfPig / iText /
/// commercial library) is license-gated and out of scope for the current
/// iteration. The shipping implementation returns
/// <see cref="EngineNotAvailableCode"/> as a deterministic
/// <see cref="Result{T}"/> failure when no engine is configured. Once an
/// engine is approved, the placeholder swaps in place — callers do not
/// change.
/// </para>
/// <para>
/// <b>No throw.</b> Implementations MUST return a typed failure rather than
/// raising — operators see the failure on the audit log / dashboard rather
/// than paging on a dead-letter queue.
/// </para>
/// </remarks>
public interface IPdfAConversionService
{
    /// <summary>Stable failure code returned when no PDF/A engine is wired in this environment.</summary>
    public const string EngineNotAvailableCode = "PDFA.ENGINE_NOT_AVAILABLE";

    /// <summary>Stable audit event code emitted when a conversion attempt fails.</summary>
    public const string AuditConversionFailed = "DOCUMENT.PDFA.CONVERSION_FAILED";

    /// <summary>Stable audit event code emitted when a conversion attempt succeeds.</summary>
    public const string AuditConversionCompleted = "DOCUMENT.PDFA.CONVERSION_COMPLETED";

    /// <summary>
    /// Converts the supplied PDF bytes to the requested PDF/A conformance
    /// level. The output bytes + lower-case hex SHA-256 land on the returned
    /// outcome envelope so the caller can persist them on the corresponding
    /// document row in one transaction.
    /// </summary>
    /// <param name="input">Input PDF + target conformance level.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>On success the populated outcome envelope; on failure a typed Result.</returns>
    Task<Result<PdfAConversionOutcomeDto>> ConvertAsync(
        PdfAConversionInputDto input,
        CancellationToken cancellationToken = default);
}
