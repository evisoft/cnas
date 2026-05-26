using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.UseCases;

/// <summary>
/// R0141 / TOR CF 15.03 — admin-only editor service backing the business-rule
/// editor UI. Surfaces CRUD on the array of business rules carried by a
/// service-passport's <c>DecisionRulesJson</c> column, addressing each rule by
/// an opaque stable id so the UI can pin operations to a specific row.
/// </summary>
/// <remarks>
/// <para>
/// <b>Stable rule id.</b> The id is NOT a database surrogate (the rules live
/// inside a JSON column, not their own table). Instead the editor derives a
/// deterministic Base32 hash of the rule's stable identity (rule name +
/// applicant type + condition JSON shape) so the same logical rule keeps the
/// same id across save round-trips. Collisions are vanishingly unlikely with
/// SHA-256 truncated to 80 bits.
/// </para>
/// <para>
/// <b>Validation before save.</b> Every mutation runs the supplied input
/// through <see cref="BusinessRuleInputDto"/>'s validator AND parses the
/// resulting full <c>DecisionRulesJson</c> body via the existing
/// <c>IDecisionEngine</c> parser. A parse failure is returned as
/// <c>ValidationFailed</c> so the caller can surface the precise error from
/// the engine (the validator's well-formed-JSON gate is the cheap fail-fast,
/// the engine parse is the deep structural check).
/// </para>
/// <para>
/// <b>Passport addressing.</b> Every method takes the stable passport
/// <c>code</c> (e.g. <c>"SP-3.1-A-BIRTH-GRANT"</c>), NOT a Sqid. The passport
/// code is the public identifier shared with external systems — the same
/// exception that applies to
/// <c>Cnas.Ps.Core.Domain.WorkflowDefinition.Code</c> and the matrix endpoint.
/// </para>
/// </remarks>
public interface IServicePassportRulesEditorService
{
    /// <summary>
    /// Returns every business rule attached to the current revision of the
    /// passport identified by <paramref name="passportCode"/>.
    /// </summary>
    /// <param name="passportCode">Stable logical passport code.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// 200 with the list (empty when no rules are configured yet); 404 when
    /// no current revision exists for <paramref name="passportCode"/>.
    /// </returns>
    Task<Result<IReadOnlyList<BusinessRuleDto>>> ListRulesAsync(
        string passportCode,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts a new business rule (when <see cref="BusinessRuleInputDto.Id"/>
    /// is null/empty) or replaces an existing one (when it resolves to a
    /// known opaque id) on the current revision of the passport.
    /// </summary>
    /// <param name="passportCode">Stable logical passport code.</param>
    /// <param name="input">The desired business-rule state.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// Success carrying the persisted rule (with the new / unchanged opaque
    /// id); <c>ValidationFailed</c> when the input fails the validator or the
    /// engine parser; <c>NotFound</c> when the passport code is unknown OR
    /// the supplied id targets a rule that no longer exists.
    /// </returns>
    Task<Result<BusinessRuleDto>> UpsertRuleAsync(
        string passportCode,
        BusinessRuleInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the business rule identified by <paramref name="ruleSqid"/>
    /// from the current revision of the passport. Idempotent on a known
    /// passport — deleting a missing rule returns <c>NotFound</c> with the
    /// rule code so the UI can refresh.
    /// </summary>
    /// <param name="passportCode">Stable logical passport code.</param>
    /// <param name="ruleSqid">Opaque stable rule id (NOT a DB Sqid).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// Success on deletion; <c>NotFound</c> when the passport or the rule
    /// is unknown.
    /// </returns>
    Task<Result> DeleteRuleAsync(
        string passportCode,
        string ruleSqid,
        CancellationToken cancellationToken = default);
}
