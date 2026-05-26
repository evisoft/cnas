using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.PublicServices;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Core.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.PublicServices;

/// <summary>
/// R0513 / TOR CF 02.01 — implementation of
/// <see cref="IExtractCnasCodeService"/>. Joins on the
/// <see cref="InsuredPerson.IdnpHash"/> shadow column (the plaintext IDNP is
/// encrypted at rest) plus <see cref="InsuredPerson.BirthDate"/> as a secondary
/// disambiguator, then synthesizes a placeholder CNAS code on match. The audit
/// row carries only the first 8 hex characters of the IDNP hash plus the
/// boolean outcome — never the raw IDNP, never a full hash that could be
/// rainbow-tabled.
/// </summary>
public sealed class ExtractCnasCodeService : IExtractCnasCodeService
{
    /// <summary>Audit event code emitted on every lookup attempt.</summary>
    public const string AuditEventCode = "PUBLIC.EXTRACT_CNAS_CODE";

    /// <summary>
    /// Number of hex characters from the IDNP hash that the audit row carries.
    /// Eight characters give 4 bytes of entropy — sufficient for forensic
    /// correlation across audit rows in the same investigation, insufficient
    /// for an attacker to brute-force back to the original IDNP. The full
    /// 44-character base64 hash is never written to the audit pipeline.
    /// </summary>
    public const int IdnpHashPrefixLength = 8;

    /// <summary>
    /// Prefix applied to the synthesized CNAS code. The deterministic shape is
    /// <c>"PA-" + Sqid(InsuredPersonId)</c> — see
    /// <see cref="IExtractCnasCodeService"/> remarks for the placeholder
    /// rationale.
    /// </summary>
    public const string CnasCodePrefix = "PA-";

    private readonly ICnasDbContext _db;
    private readonly ICaptchaVerifier _captcha;
    private readonly IDeterministicHasher _idHasher;
    private readonly ISqidService _sqids;
    private readonly IAuditService _audit;
    private readonly ICallerContext _caller;

    /// <summary>Constructs the service with its collaborators.</summary>
    /// <param name="db">EF Core context for InsuredPerson lookups.</param>
    /// <param name="captcha">Captcha verifier wired into the public anonymous surface.</param>
    /// <param name="idHasher">Deterministic IDNP hasher (shared with the InsuredPerson registration path).</param>
    /// <param name="sqids">Sqid encoder used to render the placeholder CNAS code.</param>
    /// <param name="audit">Audit-log façade — every lookup writes one Notice row.</param>
    /// <param name="caller">Per-request caller context — anonymous on this surface, but used for source-IP + correlation.</param>
    public ExtractCnasCodeService(
        ICnasDbContext db,
        ICaptchaVerifier captcha,
        IDeterministicHasher idHasher,
        ISqidService sqids,
        IAuditService audit,
        ICallerContext caller)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(captcha);
        ArgumentNullException.ThrowIfNull(idHasher);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(caller);
        _db = db;
        _captcha = captcha;
        _idHasher = idHasher;
        _sqids = sqids;
        _audit = audit;
        _caller = caller;
    }

    /// <inheritdoc />
    public async Task<Result<ExtractCnasCodeResultDto>> LookupAsync(
        ExtractCnasCodeLookupDto request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 1. CAPTCHA — short-circuit abuse before any DB query.
        var captchaResult = await _captcha
            .VerifyAsync(request.CaptchaToken, _caller.SourceIp, ct)
            .ConfigureAwait(false);
        if (captchaResult.IsFailure)
        {
            return Result<ExtractCnasCodeResultDto>.Failure(
                captchaResult.ErrorCode!,
                captchaResult.ErrorMessage!);
        }

        // 2. IDNP — must be syntactically valid (13 digits + checksum). Malformed
        //    input is a client-side bug, not an enumeration probe, so it returns
        //    InvalidIdnp rather than collapsing into the Found=false bucket.
        var idnpResult = Idnp.TryCreate(request.Idnp);
        if (idnpResult.IsFailure)
        {
            return Result<ExtractCnasCodeResultDto>.Failure(
                idnpResult.ErrorCode!,
                idnpResult.ErrorMessage!);
        }

        // 3. Compute the deterministic IDNP hash for the shadow-column lookup.
        //    The plaintext IDNP column is encrypted at rest, so equality
        //    queries MUST go through IdnpHash. The full hash is kept ONLY in
        //    memory — only its first 8 characters reach the audit pipeline.
        var idnpHash = _idHasher.ComputeHash(idnpResult.Value.Value);
        var idnpHashPrefix = idnpHash.Length >= IdnpHashPrefixLength
            ? idnpHash[..IdnpHashPrefixLength]
            : idnpHash;

        // 4. Join on IdnpHash + IsActive + BirthDate. A mismatch on any of the
        //    three collapses to Found=false (anti-enumeration). We use the
        //    InsuredPerson table because it carries both the IDNP hash and the
        //    BirthDate — Solicitant has the hash but no DOB.
        var match = await _db.InsuredPersons
            .Where(p =>
                p.IdnpHash == idnpHash
                && p.IsActive
                && p.BirthDate == request.DateOfBirth)
            .Select(p => new { p.Id })
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);

        bool found = match is not null;

        // 5. Audit row — only the hash prefix and the boolean outcome.
        var details = JsonSerializer.Serialize(new
        {
            idnpHashPrefix,
            found,
        });
        await _audit.RecordAsync(
            eventCode: AuditEventCode,
            severity: AuditSeverity.Notice,
            actorId: _caller.UserSqid ?? "anonymous",
            targetEntity: found ? nameof(InsuredPerson) : null,
            targetEntityId: match?.Id,
            detailsJson: details,
            sourceIp: _caller.SourceIp,
            correlationId: _caller.CorrelationId,
            cancellationToken: ct).ConfigureAwait(false);

        if (!found)
        {
            return Result<ExtractCnasCodeResultDto>.Success(new ExtractCnasCodeResultDto(
                Found: false,
                CnasCode: null));
        }

        // 6. Synthesize the placeholder CNAS code. The real production source
        //    will replace this in a later batch; the public DTO shape is
        //    stable so callers won't need to change.
        var cnasCode = CnasCodePrefix + _sqids.Encode(match!.Id);
        return Result<ExtractCnasCodeResultDto>.Success(new ExtractCnasCodeResultDto(
            Found: true,
            CnasCode: cnasCode));
    }
}
