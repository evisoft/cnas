using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.External;

/// <summary>
/// SIAÎSȘ — Sistemul Informațional Automatizat „Înregistrarea Șomerilor" (Automated
/// Unemployment Registration System). Source of unemployment registration status for
/// natural persons.
/// </summary>
/// <remarks>
/// Routes through MConnect using service code <c>SIAISS.GetUnemploymentStatus</c>. See
/// TOR §2.1 (item 7 in the list of 11 external systems). A <c>null</c> payload is a valid
/// "no unemployment record on file" answer and is not a failure.
/// </remarks>
public interface ISiaIssClient
{
    /// <summary>
    /// Retrieves the current unemployment-registration status for the supplied IDNP,
    /// or <c>null</c> when SIAÎSȘ has no record.
    /// </summary>
    /// <param name="idnp">Insured person's IDNP.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// <see cref="Result{T}"/> wrapping the optional status record.
    /// </returns>
    Task<Result<SiaIssUnemploymentStatus?>> GetUnemploymentAsync(string idnp, CancellationToken ct = default);
}

/// <summary>
/// Unemployment-registration snapshot returned by <see cref="ISiaIssClient"/>.
/// </summary>
/// <param name="IsRegistered">True when the person is currently registered as unemployed.</param>
/// <param name="RegisteredOn">Date of registration (when <paramref name="IsRegistered"/> is true).</param>
/// <param name="UnregisteredOn">Date of unregistration, when no longer registered.</param>
/// <param name="ReceivesAllowance">True when the person is receiving an unemployment allowance.</param>
public sealed record SiaIssUnemploymentStatus(bool IsRegistered, DateOnly? RegisteredOn, DateOnly? UnregisteredOn, bool ReceivesAllowance);
