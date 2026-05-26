using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.UseCases;

/// <summary>UC17 — Manage metadata + classifiers + document templates.</summary>
public interface IClassifierService
{
    /// <summary>Returns rows of a classifier kind (e.g. <c>CAEM</c>, <c>CUATM</c>).</summary>
    Task<Result<IReadOnlyList<ClassifierRow>>> ListAsync(string kind, CancellationToken cancellationToken = default);

    /// <summary>Inserts or updates a single classifier row.</summary>
    Task<Result> UpsertAsync(ClassifierRow row, CancellationToken cancellationToken = default);

    /// <summary>
    /// R0402 / TOR CF 17.09 — soft-deactivates an existing classifier row
    /// (flips <c>IsActive=false</c>) after asserting nothing in the system
    /// still references the <c>(kind, code)</c> pair. References are
    /// counted by an injected <c>IClassifierReferenceGuard</c>; a non-zero
    /// count short-circuits with <see cref="ErrorCodes.ClassifierReferenced"/>
    /// and the row is NOT mutated.
    /// </summary>
    /// <param name="kind">Classifier scheme code (e.g. <c>CAEM</c>).</param>
    /// <param name="code">Classifier code value within the scheme.</param>
    /// <param name="cancellationToken">Co-operative cancellation.</param>
    /// <returns>
    /// <see cref="Result.Success()"/> when the row was deactivated;
    /// <see cref="ErrorCodes.NotFound"/> when no matching row exists;
    /// <see cref="ErrorCodes.ClassifierReferenced"/> when one or more
    /// citing rows would be orphaned.
    /// </returns>
    Task<Result> DeactivateAsync(string kind, string code, CancellationToken cancellationToken = default);
}
