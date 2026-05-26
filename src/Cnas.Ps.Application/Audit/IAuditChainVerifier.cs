using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Audit;

/// <summary>
/// R0194 / SEC 047 — service that verifies the SHA-256 hash chain that
/// <c>AuditDrainer</c> and the archive-replay job write across every
/// <see cref="Cnas.Ps.Core.Domain.AuditLog"/> row. A break in the chain means
/// either a row was edited retroactively (tampering) or a row was deleted
/// (gap); both are reported with the first broken row's id and a stable reason
/// code so operators and integration tests can fail loudly.
/// </summary>
/// <remarks>
/// <para>
/// The verifier walks rows in <c>AuditLog.Id</c> order, starting from the
/// genesis literal (<c>"GENESIS"</c>) and recomputing the expected row hash
/// at each step from the previous row's stored
/// <see cref="Cnas.Ps.Core.Domain.AuditLog.RowHash"/>. The check is pure
/// read — it does not mutate the chain.
/// </para>
/// </remarks>
public interface IAuditChainVerifier
{
    /// <summary>
    /// Verifies the full audit-log hash chain, returning the report on the
    /// outcome. The wrapping <see cref="Result{T}"/> reserves the failure path
    /// for unexpected technical errors (e.g. the read context is unreachable);
    /// a chain that is structurally broken still returns
    /// <see cref="Result{T}.Success(T)"/> with
    /// <see cref="AuditChainVerificationReport.IsValid"/> set to <c>false</c>.
    /// </summary>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    /// <returns>The verification report wrapped in a <see cref="Result{T}"/>.</returns>
    Task<Result<AuditChainVerificationReport>> VerifyAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Outcome of an <see cref="IAuditChainVerifier.VerifyAsync"/> call. R0194 / SEC 047.
/// </summary>
/// <param name="IsValid">
/// <c>true</c> when the chain is intact from the first row to the last;
/// <c>false</c> when the walk found a broken link or a tampered row.
/// </param>
/// <param name="CheckedCount">
/// Number of rows the verifier walked before stopping. On a clean chain this
/// equals the total row count; on a break it equals the position of the first
/// broken row (one-based, since the walker increments before validating).
/// </param>
/// <param name="FirstBrokenRowId">
/// <c>AuditLog.Id</c> of the first row that failed validation, or <c>null</c>
/// when the chain is intact.
/// </param>
/// <param name="FirstBrokenReason">
/// Stable reason code describing the break: <c>"PrevHashMismatch"</c> when
/// the row's <see cref="Cnas.Ps.Core.Domain.AuditLog.PrevHash"/> does not
/// equal the previous row's stored hash; <c>"RowHashMismatch"</c> when the
/// recomputed digest does not equal the row's stored
/// <see cref="Cnas.Ps.Core.Domain.AuditLog.RowHash"/>. <c>null</c> when the
/// chain is intact.
/// </param>
public sealed record AuditChainVerificationReport(
    bool IsValid,
    long CheckedCount,
    long? FirstBrokenRowId,
    string? FirstBrokenReason);
