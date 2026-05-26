using System;
using System.Collections.Generic;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Core.ValueObjects;

/// <summary>
/// R0016 — generic, allocation-light guard for enum state-machine transitions.
/// Replaces hand-rolled <c>if</c>-ladders inside services with a single declarative
/// table whose contents document the allowed edges of the lifecycle. Stateless and
/// thread-safe — instances should be created once (e.g. as <c>static readonly</c>
/// fields) and reused for every check.
/// </summary>
/// <typeparam name="TStatus">
/// The enum type whose values represent the lifecycle states. Constrained to
/// <c>struct, Enum</c> so the table cannot be parameterised by a non-enum value type.
/// </typeparam>
/// <remarks>
/// <para>
/// <b>Design contract.</b> The table holds a read-only map "from-state →
/// set-of-allowed-to-states". A transition is permitted if-and-only-if the target
/// state appears in the set for the source state. Self-loops (<c>from == to</c>)
/// follow the same rule — they are allowed only when the table explicitly lists
/// them, mirroring the behaviour of the legacy guards that rejected idempotent
/// re-writes by default.
/// </para>
/// <para>
/// <b>Returned codes.</b> The Boolean <see cref="CanTransition"/> overload is the
/// hot-path predicate; the richer <see cref="Validate"/> wraps the same check in a
/// <see cref="Result"/> carrying the stable error code
/// <see cref="IllegalTransitionCode"/> ("STATUS.ILLEGAL_TRANSITION") so service
/// callers can short-circuit with a uniform failure shape. The message embeds the
/// enum type name and the from / to names for diagnosability.
/// </para>
/// <para>
/// <b>Why a Core type.</b> The guard touches no infrastructure (no DB, no clock,
/// no audit) — it is pure value logic and therefore lives in the
/// <c>Cnas.Ps.Core.Domain</c> layer per the layered-architecture rules in
/// CLAUDE.md §1.1. Services in higher layers create instances and consult them
/// before performing mutations.
/// </para>
/// </remarks>
public sealed class StatusTransitionTable<TStatus>
    where TStatus : struct, Enum
{
    /// <summary>
    /// Stable error code surfaced by <see cref="Validate"/> on a denial. Stable
    /// across versions per CLAUDE.md §2.2 — renaming is a breaking change. Kept on
    /// the generic type so each closed-generic flavour shares the same constant.
    /// </summary>
    public const string IllegalTransitionCode = "STATUS.ILLEGAL_TRANSITION";

    private readonly IReadOnlyDictionary<TStatus, IReadOnlySet<TStatus>> _allowed;

    /// <summary>
    /// Builds the table from the supplied "from → allowed-to-set" map.
    /// </summary>
    /// <param name="allowed">
    /// Read-only map describing, for each "from" state, the exhaustive set of
    /// permitted "to" states. States NOT present as keys are treated as terminal —
    /// every transition out of them is denied. States present as keys with an
    /// empty set are also terminal (equivalent, but explicit).
    /// </param>
    /// <exception cref="ArgumentNullException">When <paramref name="allowed"/> is null.</exception>
    public StatusTransitionTable(IReadOnlyDictionary<TStatus, IReadOnlySet<TStatus>> allowed)
    {
        ArgumentNullException.ThrowIfNull(allowed);
        _allowed = allowed;
    }

    /// <summary>
    /// Returns <c>true</c> when the table permits the supplied <paramref name="from"/>
    /// → <paramref name="to"/> transition; <c>false</c> otherwise. Suitable for the
    /// hot path — no allocations, single dictionary + set lookup.
    /// </summary>
    /// <param name="from">Current state.</param>
    /// <param name="to">Proposed next state.</param>
    /// <returns><c>true</c> when the edge is in the table; <c>false</c> otherwise.</returns>
    public bool CanTransition(TStatus from, TStatus to)
    {
        if (!_allowed.TryGetValue(from, out var allowedTargets))
        {
            return false;
        }

        return allowedTargets.Contains(to);
    }

    /// <summary>
    /// Validates the supplied transition and returns a <see cref="Result"/> carrying
    /// the stable failure code <see cref="IllegalTransitionCode"/> on denial.
    /// </summary>
    /// <param name="from">Current state.</param>
    /// <param name="to">Proposed next state.</param>
    /// <returns>
    /// <see cref="Result.Success"/> when the edge is allowed; otherwise a failure
    /// whose <see cref="Result.ErrorCode"/> is <see cref="IllegalTransitionCode"/>
    /// and whose <see cref="Result.ErrorMessage"/> names the enum and the two
    /// states for diagnostics.
    /// </returns>
    public Result Validate(TStatus from, TStatus to)
    {
        if (CanTransition(from, to))
        {
            return Result.Success();
        }

        var message = string.Concat(
            "Illegal ",
            typeof(TStatus).Name,
            " transition: ",
            from.ToString(),
            " → ",
            to.ToString(),
            ".");
        return Result.Failure(IllegalTransitionCode, message);
    }
}
