using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.UseCases;

/// <summary>UC07 — Register form. Server-side intake validation before workflow start.</summary>
public interface IFormIntakeService
{
    /// <summary>Validates an incoming form payload against the ServicePassport's schema.</summary>
    Task<Result> ValidateAsync(string servicePassportId, string formPayloadJson, CancellationToken cancellationToken = default);
}
