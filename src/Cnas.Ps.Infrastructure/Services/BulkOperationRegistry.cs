using System.Collections.Frozen;
using System.Text.RegularExpressions;
using Cnas.Ps.Application.BulkActions;
using Cnas.Ps.Application.Validators;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// R0166 / TOR CF 03.11 / UI 015 — frozen-dictionary <see cref="IBulkOperationRegistry"/>
/// implementation. Built once at startup from the registered set of
/// <see cref="IBulkOperation"/> instances; lookups are O(1) with no per-call hashing
/// allocation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Validation at startup.</b> The constructor rejects duplicate codes and codes
/// that do not match the canonical operation-code regex. A misconfigured registration
/// fails the host build rather than surfacing at runtime.
/// </para>
/// <para>
/// <b>Registry-name matching.</b> Each operation's <see cref="IBulkOperation.Registry"/>
/// is verified against <see cref="BulkRegistries"/> so a typo in an operation
/// declaration is caught at startup.
/// </para>
/// </remarks>
public sealed class BulkOperationRegistry : IBulkOperationRegistry
{
    private readonly FrozenDictionary<string, IBulkOperation> _dispatch;
    private readonly IReadOnlyList<IBulkOperation> _ordered;

    /// <summary>
    /// Constructs the registry from the registered <see cref="IBulkOperation"/>
    /// instances. Validates uniqueness, code shape, and registry membership.
    /// </summary>
    /// <param name="operations">Every operation registered in DI.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when an operation's code is malformed, the code is already registered,
    /// or the operation targets an unknown registry. Surfaces at host startup.
    /// </exception>
    public BulkOperationRegistry(IEnumerable<IBulkOperation> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);

        var ordered = operations.ToList();
        var dispatch = new Dictionary<string, IBulkOperation>(StringComparer.Ordinal);
        foreach (var op in ordered)
        {
            if (string.IsNullOrWhiteSpace(op.Code) || !BulkActionPatterns.OperationCode.IsMatch(op.Code))
            {
                throw new InvalidOperationException(
                    $"Bulk operation '{op.GetType().FullName}' declares an invalid code '{op.Code}'.");
            }
            if (!BulkRegistries.IsKnown(op.Registry))
            {
                throw new InvalidOperationException(
                    $"Bulk operation '{op.Code}' targets unknown registry '{op.Registry}'.");
            }
            if (!dispatch.TryAdd(op.Code, op))
            {
                throw new InvalidOperationException(
                    $"Duplicate bulk operation code '{op.Code}' — every IBulkOperation must have a unique code.");
            }
        }

        _dispatch = dispatch.ToFrozenDictionary(StringComparer.Ordinal);
        _ordered = ordered;
    }

    /// <inheritdoc />
    public bool TryGet(string code, out IBulkOperation op)
    {
        if (code is null || !_dispatch.TryGetValue(code, out var hit))
        {
            op = null!;
            return false;
        }
        op = hit;
        return true;
    }

    /// <inheritdoc />
    public IReadOnlyList<IBulkOperation> List() => _ordered;
}
