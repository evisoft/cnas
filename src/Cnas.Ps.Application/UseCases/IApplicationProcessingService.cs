using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.UseCases;

/// <summary>UC21 — Process an application/form (system actor advances workflow).</summary>
public interface IApplicationProcessingService
{
    /// <summary>Advances the workflow for the given application.</summary>
    Task<Result> AdvanceAsync(string applicationId, CancellationToken cancellationToken = default);
}
