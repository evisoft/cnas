using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.UseCases;

/// <summary>UC02 — informational calculators offered by the public interface (CF 02.01-02.05).</summary>
public interface IInformationServices
{
    /// <summary>Calculator vârstei de pensionare — returns the legal retirement age for a person.</summary>
    Task<Result<RetirementAgeOutput>> CalculateRetirementAgeAsync(
        RetirementAgeInput input,
        CancellationToken cancellationToken = default);

    /// <summary>Returns a sanitised status for a Sqid-referenced application/decision (no PII).</summary>
    Task<Result<ApplicationStatusOutput>> GetApplicationStatusAsync(
        string referenceNumber,
        CancellationToken cancellationToken = default);
}
