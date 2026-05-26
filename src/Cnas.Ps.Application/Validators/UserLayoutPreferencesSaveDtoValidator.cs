using System.Text.RegularExpressions;
using Cnas.Ps.Contracts;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R0535 / CF 04.07-08 — validates the write-side input for the user layout
/// preferences endpoint. Pins three invariants:
/// <list type="bullet">
///   <item><c>DefaultPageSize</c> ∈ [10, 200] — guards against UI-driven runaway memory.</item>
///   <item>Per-grid <c>PageSize</c> ∈ [10, 200] (when non-null) — same rationale.</item>
///   <item>Grid keys match a stable kebab-case pattern — protects the JSON shape from
///     drifting into uppercase or path-injection-style keys.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// <b>Grid-key registry intentionally omitted.</b> The validator only enforces the
/// shape regex; verifying that each key matches a known grid (e.g. <c>solicitants</c>,
/// <c>cereri</c>, <c>tasks</c>) is the registry's responsibility and is checked
/// service-side via a soft log rather than a hard reject — the front-end can save
/// preferences for grids that haven't been registered yet (e.g. an in-flight feature
/// flag) without bouncing through a validator update.
/// </para>
/// </remarks>
public sealed class UserLayoutPreferencesSaveDtoValidator
    : AbstractValidator<UserLayoutPreferencesSaveDto>
{
    /// <summary>Minimum page size accepted at either tier (system default + per-grid override).</summary>
    public const int MinPageSize = 10;

    /// <summary>Maximum page size accepted at either tier — bound on payload + render cost.</summary>
    public const int MaxPageSize = 200;

    /// <summary>
    /// Grid-key shape: lowercase ASCII letter first, followed by 2..63 lowercase / digit /
    /// dot / dash characters. The pattern is anchored on both ends so a path-traversal-style
    /// key (e.g. <c>../etc/passwd</c>) is rejected outright.
    /// </summary>
    public static readonly Regex GridKeyPattern = new(
        @"^[a-z][a-z0-9.\-]{2,63}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(50));

    /// <summary>Constructs the validator and registers every rule.</summary>
    public UserLayoutPreferencesSaveDtoValidator()
    {
        RuleFor(x => x.DefaultPageSize)
            .InclusiveBetween(MinPageSize, MaxPageSize)
            .WithMessage($"DefaultPageSize must be between {MinPageSize} and {MaxPageSize}.");

        // Grid dictionary may be null on the wire (the model-binder will tolerate it) —
        // the service normalises to an empty map. The validator therefore guards each
        // value only when the dictionary itself is non-null.
        RuleFor(x => x.Grids)
            .Custom((grids, ctx) =>
            {
                if (grids is null)
                {
                    return;
                }

                foreach (var kv in grids)
                {
                    if (string.IsNullOrEmpty(kv.Key) || !GridKeyPattern.IsMatch(kv.Key))
                    {
                        ctx.AddFailure(
                            $"Grids[{kv.Key}]",
                            "Grid key must match ^[a-z][a-z0-9.-]{2,63}$.");
                    }

                    if (kv.Value is null)
                    {
                        ctx.AddFailure(
                            $"Grids[{kv.Key}]",
                            "Grid layout value must not be null.");
                        continue;
                    }

                    if (kv.Value.PageSize is int ps && (ps < MinPageSize || ps > MaxPageSize))
                    {
                        ctx.AddFailure(
                            $"Grids[{kv.Key}].PageSize",
                            $"PageSize must be between {MinPageSize} and {MaxPageSize}.");
                    }
                }
            });

        // DashboardWidgetOrder may be null on the wire (the service normalises to empty);
        // when present every entry must be non-blank so we never persist whitespace.
        RuleForEach(x => x.DashboardWidgetOrder)
            .NotEmpty()
            .When(x => x.DashboardWidgetOrder is not null)
            .WithMessage("Dashboard widget codes must be non-empty.");
    }
}
