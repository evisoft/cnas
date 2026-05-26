using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Prefill;

/// <summary>
/// R0552 / R0562 / TOR CF 06.03 + CF 07.03 — pre-fill service that queries the three
/// upstream registries (RSP, RSUD, SI SFS) in parallel and returns a normalised
/// per-field payload that the citizen-facing or staff-facing application-form UI can
/// use to populate inputs without forcing the citizen to re-type known data.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two endpoints, one service.</b> R0552 (UC06) is the citizen self-service path —
/// "fill in my own form for me". R0562 (UC07) is the staff path — "fill in this
/// citizen's form for me". Both share the same wire shape and the same merge logic;
/// they differ only in how the target Solicitant is resolved (current caller vs.
/// supplied id) and in the permission gate (the staff path requires
/// <see cref="ForAnyApplicantPermission"/>).
/// </para>
/// <para>
/// <b>Source priorities + conflict resolution.</b> See <c>PrefillSourcePriority</c>
/// and <c>PrefillSourceAllowList</c> in this folder for the rules. RSP wins over
/// SI_SFS wins over RSUD; a same-field conflict produces a Warning naming the
/// discarded value.
/// </para>
/// <para>
/// <b>Failure semantics.</b> Per-gateway failures (timeout, network error) do NOT
/// fail the whole call — the failed source simply contributes nothing and a Warning
/// is added. The whole call returns <see cref="Result{T}.Failure"/> only when the
/// caller is unauthorised, lacks the staff permission, or the target Solicitant
/// does not exist.
/// </para>
/// <para>
/// <b>Audit.</b> Every successful call writes one Sensitive
/// <c>PREFILL.RETRIEVED</c> audit row carrying the solicitant Sqid, the sources
/// queried, and the field count — never the field values themselves (PII).
/// </para>
/// <para>
/// <b>What is deferred.</b> Real RSP/RSUD/SI SFS SOAP integrations are NDA-gated
/// (deferred per TOR §1.4); the underlying gateways resolve to the R0363 mocks in
/// production until WSDLs land. The Blazor one-click pre-fill UX (CF 06.03 button on
/// every application form) and per-field user-confirmation gate are also deferred —
/// this service ships the API, the UI will catch up in a separate iteration.
/// </para>
/// </remarks>
public interface IPrefillService
{
    /// <summary>
    /// Stable permission code required to call <see cref="PrefillForSolicitantAsync"/>.
    /// Matches the naming pattern used by other "for-any-applicant" surfaces
    /// (e.g. <c>PersonalAccount.ReadAny</c>).
    /// </summary>
    public const string ForAnyApplicantPermission = "Prefill.ForAnyApplicant";

    /// <summary>
    /// R0552 — pre-fill the calling citizen's own application form. Resolves the
    /// caller's <c>Solicitant</c> via the canonical UserProfile→Solicitant identity
    /// link (matched on <c>NationalIdHash</c>) and pulls the requested fields from
    /// the requested sources.
    /// </summary>
    /// <param name="request">
    /// Optional source / field allow-list. Null or empty lists default to
    /// "all sources" and "all fields the queried sources are willing to give".
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// On success the populated <see cref="PrefillPayloadDto"/>; when the caller is
    /// anonymous <see cref="ErrorCodes.Unauthorized"/>; when the caller has no
    /// linked Solicitant on file <see cref="ErrorCodes.NotFound"/>; when validation
    /// fails <see cref="ErrorCodes.ValidationFailed"/>.
    /// </returns>
    Task<Result<PrefillPayloadDto>> PrefillForCurrentUserAsync(
        PrefillRequestDto request,
        CancellationToken ct = default);

    /// <summary>
    /// R0562 — pre-fill the application form on behalf of the supplied applicant.
    /// Restricted to callers holding <see cref="ForAnyApplicantPermission"/>
    /// (administrator / utilizator-autorizat). Useful for back-office assistance
    /// when a citizen calls / visits a CNAS branch.
    /// </summary>
    /// <param name="solicitantId">Raw bigint id of the target Solicitant.</param>
    /// <param name="request">
    /// Optional source / field allow-list — same shape as the citizen-side call.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// On success the populated <see cref="PrefillPayloadDto"/>; when the caller
    /// lacks the permission <see cref="ErrorCodes.Forbidden"/>; when the Solicitant
    /// does not exist <see cref="ErrorCodes.NotFound"/>; when validation fails
    /// <see cref="ErrorCodes.ValidationFailed"/>.
    /// </returns>
    Task<Result<PrefillPayloadDto>> PrefillForSolicitantAsync(
        long solicitantId,
        PrefillRequestDto request,
        CancellationToken ct = default);
}
