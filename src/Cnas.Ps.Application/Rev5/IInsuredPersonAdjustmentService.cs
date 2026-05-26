using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Rev5;

/// <summary>
/// R0913 / TOR BP 2.2-D — service façade for per-insured-person contribution
/// adjustments sourced from non-REV-5 supporting documents (court decisions,
/// audit reports, individual social-insurance contracts, "other"). Every
/// successful create persists an
/// <c>InsuredPersonContributionAdjustment</c> aggregate AND projects a
/// matching <c>PersonalAccountEntry</c> with
/// <c>SourceCode = SourceDocumentCode</c>.
/// </summary>
/// <remarks>
/// <para>
/// Source-document allow-list (validator-enforced):
/// <c>"CourtDecision"</c>, <c>"AdminControl"</c>,
/// <c>"IndividualContract"</c>, <c>"Other"</c>.
/// </para>
/// </remarks>
public interface IInsuredPersonAdjustmentService
{
    /// <summary>
    /// R0913 / BP 2.2-D — creates an adjustment and projects the
    /// corresponding personal-account entry. Audit Notice
    /// <c>INSURED_PERSON.CONTRIBUTION_ADJUSTED</c>.
    /// </summary>
    /// <param name="input">Validated input envelope.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// On success the persisted DTO; on unknown Solicitant
    /// <see cref="ErrorCodes.NotFound"/>; on validation failure
    /// <see cref="ErrorCodes.ValidationFailed"/>.
    /// </returns>
    Task<Result<InsuredPersonContributionAdjustmentDto>> CreateAsync(
        InsuredPersonContributionAdjustmentInputDto input,
        CancellationToken ct = default);
}
