using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Contributors;

/// <summary>
/// R0311 / ARH 028 — service façade owning change-traceable child rows for an
/// <c>InsuredPerson</c> (Persoană asigurată). Mirrors the supersession pattern used
/// by <see cref="Payers.IPayerLinkedEntitiesService"/>.
/// </summary>
public interface IContributorLinkedEntitiesService
{
    /// <summary>Replaces the current address row by supersession.</summary>
    /// <param name="contributorId">Internal InsuredPerson id.</param>
    /// <param name="input">New address payload.</param>
    /// <param name="changeReason">Free-text rationale.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Result<ContributorAddressDto>> UpdateAddressAsync(
        long contributorId,
        ContributorAddressInputDto input,
        string? changeReason,
        CancellationToken ct = default);

    /// <summary>Replaces the current contact row by supersession.</summary>
    /// <param name="contributorId">Internal InsuredPerson id.</param>
    /// <param name="input">New contact payload.</param>
    /// <param name="changeReason">Free-text rationale.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Result<ContributorContactDto>> UpdateContactAsync(
        long contributorId,
        ContributorContactInputDto input,
        string? changeReason,
        CancellationToken ct = default);

    /// <summary>Inserts a new activity period.</summary>
    /// <param name="contributorId">Internal InsuredPerson id.</param>
    /// <param name="input">Activity-period payload.</param>
    /// <param name="changeReason">Free-text rationale.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Result<ContributorActivityPeriodDto>> AddActivityPeriodAsync(
        long contributorId,
        ContributorActivityPeriodInputDto input,
        string? changeReason,
        CancellationToken ct = default);

    /// <summary>Ends an open activity period.</summary>
    /// <param name="activityPeriodId">Internal id of the activity-period row.</param>
    /// <param name="changeReason">Free-text rationale.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Result> EndActivityPeriodAsync(
        long activityPeriodId,
        string? changeReason,
        CancellationToken ct = default);

    /// <summary>Updates the current civil-status row by supersession.</summary>
    /// <param name="contributorId">Internal InsuredPerson id.</param>
    /// <param name="input">Civil-status payload.</param>
    /// <param name="changeReason">Free-text rationale.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Result<ContributorCivilStatusDto>> UpdateCivilStatusAsync(
        long contributorId,
        ContributorCivilStatusInputDto input,
        string? changeReason,
        CancellationToken ct = default);

    /// <summary>Updates the current social-insurance contract row by supersession.</summary>
    /// <param name="contributorId">Internal InsuredPerson id.</param>
    /// <param name="input">Contract payload.</param>
    /// <param name="changeReason">Free-text rationale.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Result<ContributorSocialInsuranceContractDto>> UpdateSocialInsuranceContractAsync(
        long contributorId,
        ContributorSocialInsuranceContractInputDto input,
        string? changeReason,
        CancellationToken ct = default);

    /// <summary>Inserts a pre-1999 Carnet de muncă historical period (read-only seed).</summary>
    /// <param name="contributorId">Internal InsuredPerson id.</param>
    /// <param name="input">Period payload.</param>
    /// <param name="changeReason">Free-text rationale.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Result<ContributorPre1999PeriodCarnetMuncaDto>> AddPre1999PeriodAsync(
        long contributorId,
        ContributorPre1999PeriodCarnetMuncaInputDto input,
        string? changeReason,
        CancellationToken ct = default);

    /// <summary>Lists historical address rows newest first.</summary>
    Task<Result<IReadOnlyList<ContributorAddressDto>>> ListAddressHistoryAsync(
        long contributorId,
        CancellationToken ct = default);

    /// <summary>Lists historical contact rows newest first.</summary>
    Task<Result<IReadOnlyList<ContributorContactDto>>> ListContactHistoryAsync(
        long contributorId,
        CancellationToken ct = default);

    /// <summary>Lists historical activity periods newest first.</summary>
    Task<Result<IReadOnlyList<ContributorActivityPeriodDto>>> ListActivityPeriodHistoryAsync(
        long contributorId,
        CancellationToken ct = default);

    /// <summary>Lists historical civil-status rows newest first.</summary>
    Task<Result<IReadOnlyList<ContributorCivilStatusDto>>> ListCivilStatusHistoryAsync(
        long contributorId,
        CancellationToken ct = default);

    /// <summary>Lists historical social-insurance contract rows newest first.</summary>
    Task<Result<IReadOnlyList<ContributorSocialInsuranceContractDto>>> ListSocialInsuranceContractHistoryAsync(
        long contributorId,
        CancellationToken ct = default);

    /// <summary>Lists pre-1999 Carnet de muncă rows newest first.</summary>
    Task<Result<IReadOnlyList<ContributorPre1999PeriodCarnetMuncaDto>>> ListPre1999PeriodsAsync(
        long contributorId,
        CancellationToken ct = default);
}
