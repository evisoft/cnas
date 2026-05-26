using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.PublicServices;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Infrastructure.Services.PublicServices;

/// <summary>
/// R0511 / TOR CF 02.01 — implementation of
/// <see cref="IMedicalCertificateStatusService"/>. Validates the captcha,
/// validates the IDNP, calls PCCM via <see cref="IPccmGateway"/>, and writes
/// one audit row per call carrying the SHA-256 hash of the certificate number
/// (never the plaintext, never the IDNP).
/// </summary>
/// <remarks>
/// <para>
/// The service is registered as scoped because it owns the per-request audit /
/// caller context. The gateway is also scoped (it wraps the request-scoped
/// MConnect client in production); the captcha verifier is scoped to match the
/// existing wiring in <c>InfrastructureServiceCollectionExtensions</c>.
/// </para>
/// </remarks>
public sealed class MedicalCertificateStatusService : IMedicalCertificateStatusService
{
    /// <summary>Audit event code emitted on every lookup attempt.</summary>
    public const string AuditEventCode = "PUBLIC.MEDICAL_CERT_LOOKUP";

    private readonly IPccmGateway _pccm;
    private readonly ICaptchaVerifier _captcha;
    private readonly IAuditService _audit;
    private readonly ICallerContext _caller;

    /// <summary>Constructs the service with its collaborators.</summary>
    /// <param name="pccm">PCCM gateway facade (stubbed in dev; real SOAP/REST in prod).</param>
    /// <param name="captcha">Captcha verifier wired into the public anonymous surface.</param>
    /// <param name="audit">Audit-log façade — every lookup writes one Notice row.</param>
    /// <param name="caller">Per-request caller context — anonymous on this surface, but used for source-IP + correlation.</param>
    public MedicalCertificateStatusService(
        IPccmGateway pccm,
        ICaptchaVerifier captcha,
        IAuditService audit,
        ICallerContext caller)
    {
        ArgumentNullException.ThrowIfNull(pccm);
        ArgumentNullException.ThrowIfNull(captcha);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(caller);
        _pccm = pccm;
        _captcha = captcha;
        _audit = audit;
        _caller = caller;
    }

    /// <inheritdoc />
    public async Task<Result<MedicalCertificateStatusDto>> LookupAsync(
        MedicalCertificateLookupDto request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 1. CAPTCHA — verify BEFORE any PCCM round-trip so abuse traffic is
        //    short-circuited at the cheapest possible step. The verifier
        //    short-circuits empty / null tokens to CaptchaTokenMissing without
        //    an HTTP call.
        var captchaResult = await _captcha
            .VerifyAsync(request.CaptchaToken, _caller.SourceIp, ct)
            .ConfigureAwait(false);
        if (captchaResult.IsFailure)
        {
            return Result<MedicalCertificateStatusDto>.Failure(
                captchaResult.ErrorCode!,
                captchaResult.ErrorMessage!);
        }

        // 2. Certificate number — non-empty + bounded length. Empty input is a
        //    client-side wiring bug, surfaced as VALIDATION_FAILED.
        if (string.IsNullOrWhiteSpace(request.CertificateNumber))
        {
            return Result<MedicalCertificateStatusDto>.Failure(
                ErrorCodes.ValidationFailed,
                "Certificate number is required.");
        }

        // 3. IDNP — must be syntactically valid before we burn a PCCM call.
        //    Malformed input is a client-side bug, not an enumeration probe,
        //    so it returns the specific InvalidIdnp code (mapped to HTTP 400)
        //    rather than collapsing into the NotFound bucket.
        var idnpResult = Idnp.TryCreate(request.Idnp);
        if (idnpResult.IsFailure)
        {
            return Result<MedicalCertificateStatusDto>.Failure(
                idnpResult.ErrorCode!,
                idnpResult.ErrorMessage!);
        }

        // 4. Hash the certificate number for the audit trail. SHA-256 is fine
        //    here — the goal is "don't store the plaintext", not "make
        //    pre-image attacks infeasible". Operators correlating dashboards
        //    can re-hash any candidate and match it against the audit row.
        var certHash = ComputeSha256Hex(request.CertificateNumber.Trim());

        // 5. PCCM lookup. Failures (MConnect down, malformed payload, ...)
        //    surface as Result.Failure. We still write the audit row so
        //    operators can chart upstream-unavailability volume.
        var pccmResult = await _pccm
            .LookupCertificateAsync(request.CertificateNumber, idnpResult.Value.Value, request.DateOfBirth, ct)
            .ConfigureAwait(false);
        if (pccmResult.IsFailure)
        {
            await WriteAuditAsync(certHash, status: "UNAVAILABLE", ct).ConfigureAwait(false);
            return Result<MedicalCertificateStatusDto>.Failure(
                pccmResult.ErrorCode ?? ErrorCodes.MConnectFailed,
                pccmResult.ErrorMessage ?? "PCCM unavailable.");
        }

        // 6. Audit + project to the public DTO. The audit row carries the
        //    HASH of the cert number, not the plaintext. The IDNP is never
        //    written to the audit pipeline.
        var status = pccmResult.Value;
        await WriteAuditAsync(certHash, status.Status, ct).ConfigureAwait(false);

        return Result<MedicalCertificateStatusDto>.Success(new MedicalCertificateStatusDto(
            CertificateNumber: status.CertificateNumber,
            Status: status.Status,
            IssuedDate: status.IssuedDate,
            ValidFromDate: status.ValidFromDate,
            ValidToDate: status.ValidToDate,
            IssuerName: status.IssuerName));
    }

    /// <summary>
    /// Writes the per-call audit row. The details payload carries the cert
    /// number HASH (not plaintext) and the resulting status code; the IDNP and
    /// any other PII are excluded by construction.
    /// </summary>
    /// <param name="certHash">SHA-256 hash of the certificate number (lower-hex).</param>
    /// <param name="status">Resulting PCCM status code (or <c>"UNAVAILABLE"</c> on failure).</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task WriteAuditAsync(string certHash, string status, CancellationToken ct)
    {
        var details = JsonSerializer.Serialize(new
        {
            certificateNumberHash = certHash,
            status,
        });
        await _audit.RecordAsync(
            eventCode: AuditEventCode,
            severity: AuditSeverity.Notice,
            actorId: _caller.UserSqid ?? "anonymous",
            targetEntity: null,
            targetEntityId: null,
            detailsJson: details,
            sourceIp: _caller.SourceIp,
            correlationId: _caller.CorrelationId,
            cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Computes the lower-hex SHA-256 hash of <paramref name="value"/>. Used
    /// for the audit-row certificate-number hash; not for cryptographic
    /// authentication.
    /// </summary>
    private static string ComputeSha256Hex(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
