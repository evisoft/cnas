using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.UseCases;

/// <summary>
/// R2190-R2200 / TOR §15.6 FLEX 006 — dynamic-entity-attributes façade.
/// Exposes a small EAV-style sidecar over existing core entities so functional
/// administrators can attach configurable metadata without a schema migration.
/// </summary>
/// <remarks>
/// <para>
/// <b>Allow-list policy.</b> The service rejects any <c>AttributeCode</c>
/// that is not in <see cref="AllowedAttributeCodes"/>. This is the gate that
/// keeps the EAV sidecar disciplined — the DB column accepts free-form text
/// by design (so per-tenant extensions are possible without a migration),
/// the policy lives here.
/// </para>
/// <para>
/// <b>External ids.</b> Every Id field in input + output DTOs is Sqid-encoded;
/// the service layer decodes <c>EntitySqid</c> to the internal 64-bit primary
/// key before touching the database (CLAUDE.md RULE 3).
/// </para>
/// </remarks>
public interface IDynamicAttributeService
{
    /// <summary>
    /// Allow-list of valid <c>AttributeCode</c> values. New codes are added
    /// in code review; arbitrary values from inbound HTTP traffic are
    /// rejected with <see cref="ErrorCodes.ValidationFailed"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Starter set:
    /// <list type="bullet">
    ///   <item><c>priority</c> — operator-tagged urgency (low / normal / high).</item>
    ///   <item><c>tag</c> — comma-separated free-form tag list.</item>
    ///   <item><c>note</c> — short administrator note (≤ 4096 chars).</item>
    ///   <item><c>kpi.color</c> — KPI widget colour code (FLEX 004 stop-gap).</item>
    /// </list>
    /// </para>
    /// </remarks>
    public static readonly IReadOnlySet<string> AllowedAttributeCodes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "priority",
            "tag",
            "note",
            "kpi.color",
        };

    /// <summary>
    /// Inserts a new EAV row, or updates the <c>Value</c> column of the
    /// existing row with the same (entityType, entityId, attributeCode) tuple.
    /// Idempotent on byte-equal values — no-op when the value matches.
    /// </summary>
    /// <param name="input">Fully-validated input DTO.</param>
    /// <param name="cancellationToken">Co-operative cancellation.</param>
    /// <returns>
    /// <see cref="Result{T}.Success(T)"/> with the resulting row DTO on success;
    /// <see cref="ErrorCodes.ValidationFailed"/> on shape or allow-list rejection;
    /// <see cref="ErrorCodes.NotFound"/> when <c>EntitySqid</c> does not decode.
    /// </returns>
    Task<Result<EntityAttributeValueDto>> SetAsync(
        SetEntityAttributeInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the value for a single (entityType, entityId, attributeCode)
    /// tuple, or <see cref="ErrorCodes.NotFound"/> when the row does not exist.
    /// </summary>
    /// <param name="entityType">Logical entity kind.</param>
    /// <param name="entitySqid">Sqid of the host entity.</param>
    /// <param name="attributeCode">Allow-listed attribute code.</param>
    /// <param name="cancellationToken">Co-operative cancellation.</param>
    /// <returns>The row DTO on success; <see cref="ErrorCodes.NotFound"/> when missing.</returns>
    Task<Result<EntityAttributeValueDto>> GetAsync(
        string entityType,
        string entitySqid,
        string attributeCode,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns every dynamic-attribute row attached to a given entity instance.
    /// </summary>
    /// <param name="entityType">Logical entity kind.</param>
    /// <param name="entitySqid">Sqid of the host entity.</param>
    /// <param name="cancellationToken">Co-operative cancellation.</param>
    /// <returns>Listing of row DTOs; empty list on no matches; never NotFound.</returns>
    Task<Result<IReadOnlyList<EntityAttributeValueDto>>> ListAsync(
        string entityType,
        string entitySqid,
        CancellationToken cancellationToken = default);
}
