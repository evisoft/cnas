using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.BulkActions;

/// <summary>
/// R0166 / TOR CF 03.11 / UI 015 — outcome of one row processed by a registered
/// <see cref="IBulkOperation"/>. Returned per row from
/// <see cref="IBulkOperation.ExecuteAsync"/>; aggregated by the runner into the
/// <c>BulkOperationRun</c> counters and (on failure) into the per-row
/// <c>FailureSummaryJson</c> array.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a record, not a <see cref="Result{T}"/>.</b> The operation pipeline tolerates
/// individual row failures by design — a 1 000-row run with two FK violations becomes
/// <c>PartiallyFailed</c> rather than aborting at row 3. Modelling per-row outcome as a
/// dedicated record (instead of <see cref="Result"/>) signals that the runner expects
/// failures to be data, not exceptions, and forces operation authors to populate the
/// stable error code + human-readable message for the failure summary.
/// </para>
/// <para>
/// <b>Stability of <see cref="ErrorCode"/>.</b> The error code is part of the public
/// API contract — UI surfaces branch on it (e.g. <c>NOT_FOUND</c> renders as "row
/// already deleted" rather than a generic failure). Operation authors should reuse
/// <see cref="ErrorCodes"/> constants where possible and document any new code
/// inline.
/// </para>
/// </remarks>
public sealed record BulkRowOutcome(bool Success, string? ErrorCode, string? Message)
{
    /// <summary>Convenience factory for a successful row outcome.</summary>
    /// <returns>A successful outcome with no error code or message.</returns>
    public static BulkRowOutcome Succeeded() => new(true, null, null);

    /// <summary>Convenience factory for a failed row outcome.</summary>
    /// <param name="errorCode">Stable error code (see <see cref="ErrorCodes"/>).</param>
    /// <param name="message">Human-readable detail; rendered in the failure summary.</param>
    /// <returns>A failed outcome carrying the supplied error code and message.</returns>
    public static BulkRowOutcome Failed(string errorCode, string message) =>
        new(false, errorCode, message);
}

/// <summary>
/// R0166 / TOR CF 03.11 / UI 015 — contract for a single bulk operation registered with
/// the system. Implementations live in the Application or Infrastructure layer
/// (depending on what collaborators they need); each is registered in DI and consumed
/// by the runner via the <see cref="IBulkOperationRegistry"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Registration model.</b> Implementations are registered as multiple-instance
/// services in DI (e.g. <c>services.AddScoped&lt;IBulkOperation, WorkflowTaskReassignBulkOperation&gt;</c>);
/// the <see cref="IBulkOperationRegistry"/> builds a frozen dispatch table at startup
/// from <see cref="IEnumerable{T}"/>.
/// </para>
/// <para>
/// <b>Per-row transactionality.</b> Implementations MUST treat each
/// <see cref="ExecuteAsync"/> call as an independent transaction — never assume rows
/// will all succeed, never accumulate cross-row state inside the implementation. The
/// runner processes rows serially (no parallelism) so implementations also don't need
/// internal thread safety beyond standard EF Core scoping.
/// </para>
/// <para>
/// <b>Discovery requirement.</b> The <see cref="Code"/> and <see cref="Registry"/>
/// properties are the only metadata the runner consults — every other gating field
/// (permission, row cap, parameter requirement) lives on the descriptor returned via
/// <see cref="IBulkOperationRegistry.List"/>. Implementations MUST surface stable,
/// non-empty values for both — the registry will throw at startup if they don't.
/// </para>
/// </remarks>
public interface IBulkOperation
{
    /// <summary>
    /// Stable operation code (e.g. <c>WorkflowTask.Reassign</c>). The runner uses
    /// this as the dispatch key; renaming is a breaking change. Must match
    /// <c>^[A-Z][A-Za-z0-9.]+$</c>.
    /// </summary>
    string Code { get; }

    /// <summary>
    /// Registry the operation targets. Must equal the <c>Registry</c> the selection
    /// was created with; the runner rejects a mismatch with
    /// <see cref="ErrorCodes.ValidationFailed"/>. Must be one of the canonical
    /// <see cref="BulkRegistries"/> codes.
    /// </summary>
    string Registry { get; }

    /// <summary>
    /// Permission code the caller must hold. The controller verifies this via
    /// <c>IAuthorizationService</c> before invoking the runner. The service layer
    /// re-checks the permission set on <see cref="ICallerContext.Roles"/> as a
    /// defence-in-depth pass.
    /// </summary>
    string RequiredPermission { get; }

    /// <summary>
    /// Maximum number of rows the runner will process for a single
    /// <c>BulkOperationRun</c>. The runner refuses to execute when the resolved row
    /// set exceeds this number with
    /// <see cref="ErrorCodes.BulkQuotaExceeded"/>. Per-operation overrides the global
    /// <c>BulkOperationOptions.MaxRowsPerRun</c> floor.
    /// </summary>
    int MaxRowsPerRun { get; }

    /// <summary>
    /// <c>true</c> when the operation requires <c>ParametersJson</c> on the run
    /// request; <c>false</c> when the operation is parameterless. The runner
    /// rejects a missing payload with
    /// <see cref="ErrorCodes.ValidationFailed"/> when the flag is set.
    /// </summary>
    bool RequiresParameters { get; }

    /// <summary>
    /// Applies the per-row side effect. Invoked once per row by the runner; the
    /// implementation is expected to load the row, validate the state transition,
    /// apply the change, write any audit entries, and return a
    /// <see cref="BulkRowOutcome"/>.
    /// </summary>
    /// <param name="rowId">Internal primary key of the row to process.</param>
    /// <param name="parametersJson">
    /// Operation-specific parameters. Always non-null when
    /// <see cref="RequiresParameters"/> is <c>true</c> (the runner short-circuits
    /// missing payloads upstream); null otherwise.
    /// </param>
    /// <param name="caller">Caller context for audit attribution.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The per-row outcome; exceptions thrown here are caught by the runner and converted to a failed outcome.</returns>
    Task<BulkRowOutcome> ExecuteAsync(
        long rowId,
        string? parametersJson,
        ICallerContext caller,
        CancellationToken ct);
}
