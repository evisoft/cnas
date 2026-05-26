using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.UseCases;

/// <summary>UC06 — Submit application (Cerere). Driven by ServicePassport.</summary>
public interface IApplicationService
{
    /// <summary>Submits a new application. Validates attachments and ServicePassport eligibility.</summary>
    Task<Result<ApplicationOutput>> SubmitAsync(
        SubmitApplicationInput input,
        CancellationToken cancellationToken = default);

    /// <summary>Loads the calling user's applications, most recent first.</summary>
    Task<Result<PagedResult<ApplicationListItemOutput>>> MineAsync(
        PageRequest page,
        CancellationToken cancellationToken = default);

    /// <summary>Loads a single application by Sqid id.</summary>
    Task<Result<ApplicationOutput>> GetAsync(string applicationId, CancellationToken cancellationToken = default);

    /// <summary>Withdraws a previously-submitted, not-yet-decided application.</summary>
    Task<Result> WithdrawAsync(string applicationId, CancellationToken cancellationToken = default);
}
