using Cnas.Ps.Contracts;

namespace Cnas.Ps.Web.Components;

/// <summary>
/// R0403 / CF 17.08 — thin Web-layer facade over the API's classifier-scheme
/// endpoint, used by the <see cref="ClassifierPicker"/> /
/// <see cref="ClassifierMultiPicker"/> Blazor components. The lookup
/// is documented to return ONLY active rows so dropdown UIs are guaranteed
/// the active-only invariant without each consumer page re-filtering on its
/// own.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a Web-layer facade?</b> The Blazor components live in the WASM
/// project and therefore cannot depend on the server-side
/// <c>Cnas.Ps.Application.UseCases.IClassifierService</c> directly. The
/// default implementation <see cref="ClassifierLookup"/> proxies through the
/// same REST surface the rest of the citizen portal uses (the
/// <c>/api/classifiers/{scheme}</c> endpoint).
/// </para>
/// <para>
/// <b>Active-only invariant.</b> The corresponding service-layer test
/// (<c>ClassifierServiceTests.ListAsync_OnlyReturnsActiveRows</c>) pins the
/// guarantee at the source — every row this facade returns has
/// <c>IsActive == true</c>.
/// </para>
/// </remarks>
public interface IClassifierLookup
{
    /// <summary>
    /// Returns the active rows of the supplied classifier scheme. The default
    /// implementation issues <c>GET /api/classifiers/{scheme}</c>; tests can
    /// substitute an in-memory fake to drive deterministic dropdown renders.
    /// </summary>
    /// <param name="scheme">Classifier scheme code (e.g. <c>CAEM</c>, <c>CUATM</c>, <c>CFOJ</c>).</param>
    /// <param name="cancellationToken">Co-operative cancellation.</param>
    /// <returns>Read-only list of active rows; never <c>null</c>.</returns>
    Task<IReadOnlyList<ClassifierRow>> GetActiveAsync(string scheme, CancellationToken cancellationToken = default);
}
