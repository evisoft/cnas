using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.UseCases;

/// <summary>
/// R2163 / INT 004 — schema-driven new-service provisioning façade. Allows a functional
/// administrator to register a brand-new electronic service in the SI-PS catalogue
/// without recompiling or running migrations: a single call accepts the schema-driven
/// definition (form fields, workflow code, decision rules, classifier references) and
/// materialises a fresh <c>ServicePassport</c> row plus a default workflow placeholder.
/// </summary>
/// <remarks>
/// <para>
/// This sits ABOVE <c>IServicePassportService</c> + <c>IWorkflowConfigurationService</c>;
/// it does NOT replace them. The traditional upsert surface remains the right tool when
/// modifying an existing passport (versioning, semantic diff, audit trail). The
/// provisioning surface is a one-shot factory for the "configurable new web service"
/// contract (TOR §15.4 INT 004): create-only, idempotent on duplicate-code (returns
/// stable failure), and emits Critical <c>SERVICE.PROVISIONED</c> + (when retired)
/// <c>SERVICE.RETIRED</c> audits.
/// </para>
/// </remarks>
public interface IServiceCatalogConfigService
{
    /// <summary>
    /// Provisions a new service-catalog entry from the schema-driven definition supplied
    /// by the caller.
    /// </summary>
    /// <param name="input">
    /// Fully-validated <see cref="NewServiceProvisionInputDto"/>. The shape contract is
    /// enforced by the <c>NewServiceProvisionInputValidator</c> at the API boundary;
    /// the service additionally cross-references the workflow code (must exist) and
    /// rejects duplicates of an already-registered passport <see cref="NewServiceProvisionInputDto.Code"/>.
    /// </param>
    /// <param name="cancellationToken">Co-operative cancellation.</param>
    /// <returns>
    /// <see cref="Result{T}.Success(T)"/> with the Sqid of the new passport on success;
    /// <see cref="ErrorCodes.Conflict"/> when the code is already registered;
    /// <see cref="ErrorCodes.ValidationFailed"/> when the JSON schema / decision rules
    /// fail to parse; <see cref="ErrorCodes.NotFound"/> when the referenced workflow code
    /// is not registered yet.
    /// </returns>
    Task<Result<NewServiceProvisionDto>> ProvisionAsync(
        NewServiceProvisionInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retires an existing service-catalog entry — soft-deactivates the current passport
    /// row (<c>IsEnabled=false</c>) and emits a Critical <c>SERVICE.RETIRED</c> audit
    /// carrying the supplied operator reason. In-flight applications already pinned to a
    /// prior version remain unaffected (the version chain preserves history).
    /// </summary>
    /// <param name="passportCode">Logical passport code (case-insensitive).</param>
    /// <param name="reason">Non-empty operator reason; audited verbatim.</param>
    /// <param name="cancellationToken">Co-operative cancellation.</param>
    /// <returns>
    /// <see cref="Result.Success()"/> when the passport was retired or already disabled
    /// (idempotent); <see cref="ErrorCodes.NotFound"/> when no current row matches the
    /// code; <see cref="ErrorCodes.ValidationFailed"/> when <paramref name="reason"/> is
    /// empty.
    /// </returns>
    Task<Result> RetireAsync(
        string passportCode,
        string reason,
        CancellationToken cancellationToken = default);
}
