using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.PublicServices;

/// <summary>
/// R0513 / TOR CF 02.01 — anonymous "find my CNAS personal-account code" lookup.
/// Drives the public, unauthenticated
/// <c>POST /api/public/extract-cnas-code</c> endpoint. The citizen supplies
/// their IDNP + DOB; the service returns the CNAS-side personal account code
/// when the (IDNP, DOB) pair matches a registered InsuredPerson, and a single
/// undifferentiated "not found" response otherwise.
/// </summary>
/// <remarks>
/// <para>
/// <b>Anti-enumeration discipline.</b> Three failure shapes collapse into a
/// single <c>Found=false</c> response: unknown IDNP, mismatched DOB,
/// soft-deleted record. Distinguishing them would let a scraper iterate the
/// IDNP space and identify enrolled citizens.
/// </para>
/// <para>
/// <b>No PII in audit.</b> The audit row carries only the first 8 characters
/// of the IDNP HMAC hash (sufficient for forensic correlation, insufficient to
/// reverse) plus the boolean outcome. The raw IDNP never reaches the audit
/// pipeline.
/// </para>
/// <para>
/// <b>Placeholder code.</b> Until the production CNAS-code source-of-truth is
/// wired, the service synthesizes a deterministic code as <c>"PA-" +
/// SqidEncode(insuredPersonId)</c>. The synthesis is documented loudly so the
/// audit forensics + UI specification know to handle the placeholder shape;
/// the real account-code source will replace this in a later batch without
/// changing the public DTO.
/// </para>
/// </remarks>
public interface IExtractCnasCodeService
{
    /// <summary>
    /// Verifies the captcha, validates the IDNP, joins on IDNP hash + DOB,
    /// and returns the synthesized CNAS code on match. Always writes one
    /// audit row.
    /// </summary>
    /// <param name="request">Lookup envelope from the public endpoint.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// On success a <see cref="ExtractCnasCodeResultDto"/> with
    /// <see cref="ExtractCnasCodeResultDto.Found"/> = <c>true</c> + CNAS code,
    /// or <c>false</c> + <c>null</c> code on any disambiguator mismatch. On
    /// captcha rejection or malformed IDNP a failed <see cref="Result{T}"/>
    /// with the appropriate stable error code.
    /// </returns>
    Task<Result<ExtractCnasCodeResultDto>> LookupAsync(
        ExtractCnasCodeLookupDto request,
        CancellationToken ct = default);
}
