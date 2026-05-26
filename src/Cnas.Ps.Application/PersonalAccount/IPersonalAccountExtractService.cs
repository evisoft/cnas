using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.PersonalAccount;

/// <summary>
/// R0516 / TOR CF 02.04 — extracts the citizen's CNAS personal account into a
/// structured JSON payload. Drives two authenticated endpoints:
/// <list type="bullet">
///   <item>Self-service <c>GET /api/self-service/personal-account/extract</c>
///   for the caller's own account (resolved server-side via
///   <c>ICallerContext</c>).</item>
///   <item>Admin / utilizator-autorizat
///   <c>GET /api/admin/personal-account/{solicitantSqid}/extract</c> gated by
///   the <c>PersonalAccount.ReadAny</c> permission.</item>
/// </list>
/// Both surfaces share the same aggregation logic so the wire shape stays
/// consistent regardless of who reads the extract.
/// </summary>
/// <remarks>
/// <para>
/// <b>Aggregation contract.</b> Entries are grouped by calendar year; each
/// year carries the sum of bases + sum of paid amounts + count of distinct
/// months. Years are sorted DESC (newest first); entries inside a year are
/// sorted ASC by month. An empty account returns a successful payload with
/// no <c>Years</c>, <c>GrandTotal=0</c>, and <c>GrandTotalMonths=0</c>.
/// </para>
/// <para>
/// <b>Audit.</b> Every successful invocation writes one Sensitive-severity
/// <c>PERSONAL_ACCOUNT.EXTRACT_GENERATED</c> audit row carrying the
/// solicitant's Sqid + the aggregate counts. The Sensitive severity is
/// inherited from "access to confidential data" per TOR SEC 038-048 — the
/// citizen's contribution history is regulated.
/// </para>
/// </remarks>
public interface IPersonalAccountExtractService
{
    /// <summary>
    /// Resolves the caller's Solicitant via the existing UserProfile→Solicitant
    /// identity link (matched on the canonical national-id hash) and returns
    /// the personal-account extract for that Solicitant.
    /// </summary>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// On success the populated <see cref="PersonalAccountExtractDto"/>; when
    /// the caller is anonymous <see cref="ErrorCodes.Unauthorized"/>; when the
    /// caller's user row carries no matching Solicitant (or the Solicitant has
    /// no PersonalAccount on file) <see cref="ErrorCodes.NotFound"/>.
    /// </returns>
    Task<Result<PersonalAccountExtractDto>> GetForCurrentUserAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the personal-account extract for the supplied Solicitant id.
    /// Restricted to callers holding the <c>PersonalAccount.ReadAny</c>
    /// permission (administrator / utilizator-autorizat). Useful for
    /// back-office assistance and inspections.
    /// </summary>
    /// <param name="solicitantId">Raw bigint id of the target Solicitant.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// On success the populated <see cref="PersonalAccountExtractDto"/>; when
    /// the caller lacks the permission <see cref="ErrorCodes.Forbidden"/>;
    /// when the Solicitant has no PersonalAccount on file
    /// <see cref="ErrorCodes.NotFound"/>.
    /// </returns>
    Task<Result<PersonalAccountExtractDto>> GetForSolicitantAsync(
        long solicitantId,
        CancellationToken ct = default);
}
