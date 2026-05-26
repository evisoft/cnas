using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.External;

/// <summary>
/// EESSI — Electronic Exchange of Social Security Information. EU-wide bus used to
/// exchange contribution and benefit periods with the social-security authorities of
/// other member states (relevant for EU pension portability).
/// </summary>
/// <remarks>
/// Routes through MConnect using service code <c>EESSI.GetSocialSecurityRecord</c>. See
/// TOR §2.1 / §2.5 (EESSI is one of the 11 external systems). Lookup is by foreign
/// member-state + foreign SSN, not by Moldovan IDNP, because EESSI subjects are by
/// definition non-Moldovan residents whose records originate abroad.
/// </remarks>
public interface IEessiClient
{
    /// <summary>
    /// Retrieves the EESSI record for a person referenced by their foreign-member-state
    /// social-security number.
    /// </summary>
    /// <param name="memberStateCode">ISO 3166 alpha-2 member-state code (e.g. "RO", "IT").</param>
    /// <param name="foreignSsn">Member-state-specific social-security number string.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// <see cref="Result{T}"/> wrapping the optional record. <c>Success(null)</c> means
    /// the foreign authority has no record for the supplied reference.
    /// </returns>
    Task<Result<EessiRecord?>> GetByForeignReferenceAsync(string memberStateCode, string foreignSsn, CancellationToken ct = default);
}

/// <summary>
/// EESSI consolidated record returned by <see cref="IEessiClient"/>.
/// </summary>
/// <param name="MemberStateCode">Echo of the requested ISO 3166 alpha-2 member-state code.</param>
/// <param name="ForeignSsn">Echo of the requested foreign SSN.</param>
/// <param name="LifetimeContributionMdlEquivalent">Sum of all contribution periods converted to MDL.</param>
/// <param name="Periods">Insurance periods reported by the foreign authority.</param>
public sealed record EessiRecord(string MemberStateCode, string ForeignSsn, decimal LifetimeContributionMdlEquivalent, IReadOnlyList<EessiPeriod> Periods);

/// <summary>
/// One insurance period reported by an EESSI member state.
/// </summary>
/// <param name="Start">Inclusive start date of the period.</param>
/// <param name="End">Inclusive end date of the period.</param>
/// <param name="CountryCode">ISO 3166 alpha-2 country of the institution holding the period.</param>
/// <param name="Status">Period status code as defined by the foreign authority (e.g. "EMPLOYED", "SELF_EMPLOYED", "VOLUNTARY").</param>
public sealed record EessiPeriod(DateOnly Start, DateOnly End, string CountryCode, string Status);
