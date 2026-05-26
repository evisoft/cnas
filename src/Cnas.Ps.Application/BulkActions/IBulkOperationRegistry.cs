namespace Cnas.Ps.Application.BulkActions;

/// <summary>
/// R0166 / TOR CF 03.11 / UI 015 — read-only registry of registered
/// <see cref="IBulkOperation"/> implementations, built once at startup from the DI
/// container. The runner consults this registry to dispatch a stable
/// <c>OperationCode</c> to its handler; the discovery endpoint returns the descriptor
/// list so a UI can render a per-registry catalog.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why frozen.</b> The dispatch table is invariant once the host has started; a
/// frozen dictionary gives O(1) lookups with no per-call hashing overhead. The
/// registry is registered as a singleton in DI.
/// </para>
/// <para>
/// <b>Validation at construction.</b> The implementation validates each operation's
/// <see cref="IBulkOperation.Code"/> against the stable regex (<c>^[A-Z][A-Za-z0-9.]+$</c>)
/// and rejects duplicates. A registration error fails the host at startup rather than
/// surfacing as a runtime mystery.
/// </para>
/// </remarks>
public interface IBulkOperationRegistry
{
    /// <summary>
    /// Attempts to resolve an operation by its stable code. Case-sensitive.
    /// </summary>
    /// <param name="code">Stable operation code (e.g. <c>WorkflowTask.Reassign</c>).</param>
    /// <param name="op">The resolved operation when the method returns <c>true</c>; otherwise undefined.</param>
    /// <returns><c>true</c> when an operation is registered for the supplied code.</returns>
    bool TryGet(string code, out IBulkOperation op);

    /// <summary>
    /// Lists every registered operation. Used by the discovery endpoint
    /// (<c>GET /api/bulk-actions/operations</c>) and by the runner's permission
    /// pre-check.
    /// </summary>
    /// <returns>An unordered snapshot of every registered operation.</returns>
    IReadOnlyList<IBulkOperation> List();
}
