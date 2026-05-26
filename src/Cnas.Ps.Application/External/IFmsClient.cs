using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.External;

/// <summary>
/// FMS — Sistemul Informațional al Ministerului Finanțelor (Treasury / Ministry of
/// Finance Information System). Source of CNAS-account balances and recent treasury
/// transactions.
/// </summary>
/// <remarks>
/// Routes through MConnect using service code <c>FMS.GetCnasAccountState</c>. See TOR
/// §2.1 / §2.5 (FMS is one of the 11 external systems CNAS-PS integrates with). The
/// lookup key is an internal CNAS reference (treasury sub-account number), not an IDNP.
/// </remarks>
public interface IFmsClient
{
    /// <summary>
    /// Retrieves the current treasury account state and recent transaction list for the
    /// supplied CNAS-internal account reference.
    /// </summary>
    /// <param name="cnasInternalRef">CNAS treasury sub-account reference (assigned by CNAS, not the citizen).</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns><see cref="Result{T}"/> wrapping the account state on success.</returns>
    Task<Result<FmsAccountState>> GetAccountStateAsync(string cnasInternalRef, CancellationToken ct = default);
}

/// <summary>
/// Treasury account snapshot returned by <see cref="IFmsClient"/>.
/// </summary>
/// <param name="CurrentBalanceMdl">Current account balance in MDL.</param>
/// <param name="AsOfUtc">UTC instant at which the balance was computed by FMS.</param>
/// <param name="RecentTransactions">Recent transactions (ordered most-recent-first by upstream convention).</param>
public sealed record FmsAccountState(decimal CurrentBalanceMdl, DateTime AsOfUtc, IReadOnlyList<FmsTransaction> RecentTransactions);

/// <summary>
/// One treasury transaction line returned by <see cref="IFmsClient"/>.
/// </summary>
/// <param name="PostedAtUtc">UTC instant at which the transaction posted in the treasury ledger.</param>
/// <param name="AmountMdl">Signed amount in MDL (positive = credit, negative = debit).</param>
/// <param name="ReferenceNumber">Treasury reference number that uniquely identifies the transaction.</param>
/// <param name="Description">Free-text description as recorded by the treasury.</param>
public sealed record FmsTransaction(DateTime PostedAtUtc, decimal AmountMdl, string ReferenceNumber, string Description);
