using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

// ────────────────────────────────────────────────────────────────────────────
// R0511 — Medical Certificate Status (anonymous lookup against PCCM)
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// R0511 / TOR CF 02.01 — input for the anonymous medical-certificate status
/// lookup. The endpoint is unauthenticated and gated by CAPTCHA, so the body
/// MUST carry every disambiguator the citizen possesses (certificate number +
/// IDNP + DOB) before the upstream PCCM call is made.
/// </summary>
/// <remarks>
/// <para>
/// <b>No echo policy.</b> The matching <see cref="MedicalCertificateStatusDto"/>
/// output never echoes the IDNP or any personal data — failures collapse to a
/// single <c>"NotFound"</c> bucket so an attacker cannot distinguish "this IDNP
/// is enrolled in PCCM" from "this IDNP exists but the certificate number is
/// wrong". See R0511 anti-enumeration discipline.
/// </para>
/// </remarks>
/// <param name="CertificateNumber">
/// PCCM-assigned certificate number (mandatory). Format is opaque to CNAS — the
/// validator only checks non-empty and bounded length; PCCM is the source of
/// truth for shape.
/// </param>
/// <param name="Idnp">
/// 13-digit Moldovan IDNP of the certificate's holder. Validated via
/// <c>Cnas.Ps.Core.ValueObjects.Idnp.TryCreate</c> at the service
/// boundary; on mismatch the service collapses to <c>Status="NotFound"</c>
/// without distinguishing "unknown IDNP" from "IDNP doesn't own this
/// certificate" (anti-enumeration).
/// </param>
/// <param name="DateOfBirth">
/// Date of birth of the certificate's holder. Used as a secondary disambiguator
/// against PCCM data; mismatched DOB collapses to <c>Status="NotFound"</c>.
/// </param>
/// <param name="CaptchaToken">
/// Anti-abuse token from the client widget (Cloudflare Turnstile in production).
/// Verified server-side via <c>Cnas.Ps.Application.Abstractions.ICaptchaVerifier</c>;
/// the raw token is never logged or echoed back.
/// </param>
public sealed record MedicalCertificateLookupDto(
    string CertificateNumber,
    [property: SensitivityClassification(SensitivityLabel.Restricted,
        Reason = "IDNP is the highest-sensitivity citizen identifier per R0228 / SEC 033.")]
    string Idnp,
    [property: SensitivityClassification(SensitivityLabel.Confidential,
        Reason = "DateOfBirth is citizen PII per R0228 / SEC 033.")]
    DateOnly DateOfBirth,
    string CaptchaToken);

/// <summary>
/// R0511 / TOR CF 02.01 — sanitised output of the medical-certificate status
/// lookup. Carries NO personal data: only the certificate's lifecycle metadata
/// and (when applicable) the issuing institution's display name.
/// </summary>
/// <param name="CertificateNumber">
/// Echo of the request's certificate number — included so the UI can render the
/// status next to the input the citizen typed. PCCM's identifier is stable and
/// non-PII.
/// </param>
/// <param name="Status">
/// Stable status code. One of: <c>"Active"</c> (within validity window),
/// <c>"Closed"</c> (validity window elapsed), <c>"Cancelled"</c> (revoked by the
/// issuer), or <c>"NotFound"</c> (no match — covers unknown certificate,
/// mismatched IDNP, and mismatched DOB collapsed into a single bucket per
/// anti-enumeration discipline).
/// </param>
/// <param name="IssuedDate">
/// Date the certificate was originally issued. <c>null</c> when
/// <see cref="Status"/> is <c>"NotFound"</c>.
/// </param>
/// <param name="ValidFromDate">
/// First date of the prescribed medical leave (inclusive). <c>null</c> when
/// <see cref="Status"/> is <c>"NotFound"</c>.
/// </param>
/// <param name="ValidToDate">
/// Last date of the prescribed medical leave (inclusive). <c>null</c> when
/// <see cref="Status"/> is <c>"NotFound"</c>.
/// </param>
/// <param name="IssuerName">
/// Display name of the issuing institution as recorded in PCCM. <c>null</c>
/// when <see cref="Status"/> is <c>"NotFound"</c> or when PCCM omitted the
/// field. Note: this is an organisation name, not a person — no PII concern.
/// </param>
public sealed record MedicalCertificateStatusDto(
    string CertificateNumber,
    string Status,
    DateOnly? IssuedDate,
    DateOnly? ValidFromDate,
    DateOnly? ValidToDate,
    string? IssuerName);

// ────────────────────────────────────────────────────────────────────────────
// R0512 — Online Appointment Booking (external scheduling deep-link)
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// R0512 / TOR CF 02.01 — directory response for the anonymous online-appointment
/// discovery endpoint. CNAS does NOT host the scheduling flow; the citizen
/// selects a regional branch and is deep-linked to the external scheduler with
/// the branch code substituted into the configured URL template.
/// </summary>
/// <param name="Branches">
/// Active CNAS regional branches in stable alphabetical-by-name order. The
/// caller renders these as a selectable list.
/// </param>
/// <param name="DeepLinkTemplate">
/// URL template for the external scheduler with a single
/// <c>{branchCode}</c> placeholder. The caller may render this for
/// informational purposes; the canonical resolution happens server-side via
/// <c>POST /api/public/appointments/{branchCode}/resolve</c>.
/// </param>
public sealed record AppointmentBookingDirectoryDto(
    IReadOnlyList<AppointmentBranchDto> Branches,
    string DeepLinkTemplate);

/// <summary>
/// R0512 / TOR CF 02.01 — one CNAS regional branch surfaced by the
/// online-appointment directory.
/// </summary>
/// <remarks>
/// <b>Stable code, not Sqid.</b> The branch <see cref="Code"/> is a hand-curated
/// short string (<c>"CHISINAU-CENTRU"</c>, <c>"BALTI"</c>, ...) — it is part of
/// the public deep-link contract and must remain readable in the URL. The
/// branch's surrogate database identifier never crosses the boundary, so
/// CLAUDE.md RULE 3 is satisfied without Sqid encoding.
/// </remarks>
/// <param name="Code">Stable, URL-safe short code of the branch.</param>
/// <param name="Name">Display name of the branch (Romanian).</param>
/// <param name="City">City the branch is located in.</param>
/// <param name="Address">Optional street address; <c>null</c> when not curated.</param>
/// <param name="Phone">Optional contact phone (E.164); <c>null</c> when not curated.</param>
public sealed record AppointmentBranchDto(
    string Code,
    string Name,
    string City,
    string? Address,
    [property: SensitivityClassification(SensitivityLabel.Confidential,
        Reason = "Phone field follows the R0228 / SEC 033 convention; even for branch directory data the canonical PhoneXxx convention bumps to Confidential.")]
    string? Phone);

/// <summary>
/// R0512 / TOR CF 02.01 — fully-rendered deep-link URL emitted by the
/// online-appointment resolver. The caller redirects the citizen to this URL;
/// any further interaction happens on the external scheduling system.
/// </summary>
/// <param name="Url">
/// Absolute URL with the branch code substituted into the configured deep-link
/// template. Never contains citizen PII — the citizen identifies themselves on
/// the scheduler side.
/// </param>
public sealed record AppointmentDeepLinkDto(string Url);

// ────────────────────────────────────────────────────────────────────────────
// R0513 — Extract CNAS Code (anonymous personal-account code lookup)
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// R0513 / TOR CF 02.01 — input for the anonymous "find my CNAS code" lookup.
/// The endpoint is unauthenticated and gated by CAPTCHA + rate-limiting + DOB
/// disambiguation to prevent IDNP-enumeration attacks.
/// </summary>
/// <param name="Idnp">
/// 13-digit Moldovan IDNP of the citizen. Validated via
/// <c>Cnas.Ps.Core.ValueObjects.Idnp.TryCreate</c>; an invalid IDNP
/// returns <c>Cnas.Ps.Core.Common.ErrorCodes.InvalidIdnp</c> rather than
/// a generic <c>NotFound</c> because malformed input is a client-side bug, not
/// an enumeration probe.
/// </param>
/// <param name="DateOfBirth">
/// Date of birth of the citizen — secondary disambiguator. A mismatch collapses
/// to <see cref="ExtractCnasCodeResultDto.Found"/> = <c>false</c> identically to
/// "IDNP unknown" so an attacker cannot distinguish the two cases.
/// </param>
/// <param name="CaptchaToken">
/// Anti-abuse token from the client widget. Verified server-side via
/// <c>Cnas.Ps.Application.Abstractions.ICaptchaVerifier</c>.
/// </param>
public sealed record ExtractCnasCodeLookupDto(
    [property: SensitivityClassification(SensitivityLabel.Restricted,
        Reason = "IDNP is the highest-sensitivity citizen identifier per R0228 / SEC 033.")]
    string Idnp,
    [property: SensitivityClassification(SensitivityLabel.Confidential,
        Reason = "DateOfBirth is citizen PII per R0228 / SEC 033.")]
    DateOnly DateOfBirth,
    string CaptchaToken);

/// <summary>
/// R0513 / TOR CF 02.01 — output of the anonymous CNAS-code lookup. Carries the
/// citizen's personal-account code on a successful match, nothing on a miss
/// (anti-enumeration).
/// </summary>
/// <param name="Found">
/// <c>true</c> when the (IDNP, DOB) pair matched a registered Solicitant + the
/// underlying InsuredPerson record. <c>false</c> on any mismatch — unknown
/// IDNP, mismatched DOB, soft-deleted record. The single <c>false</c> bucket
/// is load-bearing for anti-enumeration.
/// </param>
/// <param name="CnasCode">
/// The citizen's personal-account code when <see cref="Found"/> is <c>true</c>;
/// otherwise <c>null</c>. Never contains the IDNP, name, or any other PII —
/// only the opaque CNAS-side reference.
/// </param>
public sealed record ExtractCnasCodeResultDto(
    bool Found,
    string? CnasCode);
