using Cnas.Ps.Application.Prefill;
using Cnas.Ps.Contracts;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R0552 / R0562 — FluentValidation rules for <see cref="PrefillRequestDto"/>. Both
/// citizen and staff endpoints share this validator; the controller / service layer
/// then layers in role / permission checks on top.
/// </summary>
/// <remarks>
/// <para>
/// <b>Rules:</b>
/// <list type="bullet">
///   <item>Every entry of <see cref="PrefillRequestDto.Sources"/> (when supplied)
///     MUST be a member of <c>PrefillSources.All</c>.</item>
///   <item>Every entry of <see cref="PrefillRequestDto.Fields"/> (when supplied)
///     MUST be a member of <c>PrefillFields.All</c>.</item>
///   <item><see cref="PrefillRequestDto.Fields"/> length MUST be ≤ 50 — guards
///     against accidental wide explosions (the vocabulary itself is bounded but
///     duplicate / pathological inputs must not consume unbounded merge work).</item>
/// </list>
/// </para>
/// <para>
/// <b>Empty / null lists are valid.</b> A null or empty <see cref="PrefillRequestDto.Sources"/>
/// means "all three sources"; a null or empty <see cref="PrefillRequestDto.Fields"/>
/// means "every field the queried sources are willing to give". The defaults are
/// applied in the service layer — the validator merely refuses unknown values.
/// </para>
/// </remarks>
public sealed class PrefillRequestDtoValidator : AbstractValidator<PrefillRequestDto>
{
    /// <summary>Hard upper bound on the per-request field allow-list length.</summary>
    public const int MaxFields = 50;

    /// <summary>Constructs the validator and registers the rule set.</summary>
    public PrefillRequestDtoValidator()
    {
        // Sources allow-list — every supplied code must be recognised. Null / empty
        // bypass the rule (defaults are applied downstream).
        RuleForEach(x => x.Sources)
            .Must(s => s is not null && PrefillSources.All.Contains(s))
            .WithMessage("Source must be one of: RSP, RSUD, SI_SFS.")
            .When(x => x.Sources is not null && x.Sources.Count > 0);

        // Fields allow-list — every supplied name must be in the frozen vocabulary.
        RuleForEach(x => x.Fields)
            .Must(f => f is not null && PrefillFields.All.Contains(f))
            .WithMessage($"Field must be one of the {PrefillFields.All.Count} known prefill field names.")
            .When(x => x.Fields is not null && x.Fields.Count > 0);

        // Fields count cap — defense-in-depth against pathological inputs even
        // though the vocabulary itself is bounded.
        RuleFor(x => x.Fields!.Count)
            .LessThanOrEqualTo(MaxFields)
            .WithMessage($"Fields list must not exceed {MaxFields} entries.")
            .When(x => x.Fields is not null);
    }
}
