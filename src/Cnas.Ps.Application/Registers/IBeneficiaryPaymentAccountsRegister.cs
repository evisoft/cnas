using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Registers;

/// <summary>
/// R1602 / TOR Annex 3.10 — read-only projection over the existing
/// <c>MPayOrder</c> aggregate that exposes the canonical
/// "Registrul conturilor de plată" (payment-accounts register) shape, keyed
/// by beneficiary IDNP. Not a separate table — the projection materialises
/// the Annex-3.10 columns from the latest payment-order rows per beneficiary.
/// </summary>
/// <remarks>
/// <para>
/// Backed by <see cref="Application.Abstractions.IReadOnlyCnasDbContext"/> so
/// the listing reads route to the Postgres streaming replica per ARH 025.
/// </para>
/// <para>
/// <b>IBAN masking.</b> The implementation MUST mask the IBAN before
/// returning it on the wire per TOR SEC 035 — never surface the full IBAN
/// on a list endpoint.
/// </para>
/// </remarks>
public interface IBeneficiaryPaymentAccountsRegister
{
    /// <summary>
    /// Lists payment-account rows, optionally narrowed to a specific
    /// beneficiary by raw IDNP. Ordering is
    /// <c>LastPaymentAtUtc DESC NULLS LAST</c>.
    /// </summary>
    /// <param name="beneficiaryIdnp">
    /// Optional raw 13-digit IDNP to narrow on. When supplied the implementation
    /// canonicalizes + deterministically hashes the value through
    /// <see cref="Application.Abstractions.IDeterministicHasher"/> and filters by
    /// the resulting shadow key (the plaintext column is encrypted with a
    /// nondeterministic nonce per row and can't be queried by equality).
    /// </param>
    /// <param name="page">1-based page index.</param>
    /// <param name="pageSize">Page size (clamped to [1, 200]).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result{T}.Success"/> with the paged result on success.
    /// </returns>
    Task<Result<PagedResult<BeneficiaryPaymentAccountRowDto>>> ListAsync(
        string? beneficiaryIdnp,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);
}
