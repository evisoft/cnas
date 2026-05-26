using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.UseCases;

/// <summary>UC14 — Exchange data with external systems. Wraps MConnect calls.</summary>
public interface IInteropService
{
    /// <summary>Calls an MConnect-published service.</summary>
    Task<Result<string>> CallAsync(string serviceCode, string requestJson, CancellationToken cancellationToken = default);
}
