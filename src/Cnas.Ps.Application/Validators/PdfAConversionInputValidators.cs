using System;
using Cnas.Ps.Contracts;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R0341 / TOR CF 11.06 — validates <see cref="PdfAConversionInputDto"/>.
/// Bounds the input size to 1..50_000_000 bytes (50 MB) and pins the
/// conformance-level enum membership.
/// </summary>
public sealed class PdfAConversionInputValidator
    : AbstractValidator<PdfAConversionInputDto>
{
    /// <summary>Minimum legal PDF size in bytes — anything smaller cannot be a real PDF.</summary>
    public const int MinSourceBytes = 1;

    /// <summary>Maximum legal PDF size in bytes (50 MB) — anything larger is rejected.</summary>
    public const int MaxSourceBytes = 50_000_000;

    /// <summary>Builds the validator.</summary>
    public PdfAConversionInputValidator()
    {
        RuleFor(x => x.SourcePdfBytes)
            .NotNull().WithMessage("SourcePdfBytes is required.")
            .Must(b => b is not null && b.Length >= MinSourceBytes && b.Length <= MaxSourceBytes)
            .WithMessage($"SourcePdfBytes length must be in [{MinSourceBytes}, {MaxSourceBytes}] bytes.");

        RuleFor(x => x.TargetConformance)
            .Must(IsKnownConformance)
            .WithMessage("TargetConformance must be a stable PdfAConformanceLevel enum value.");
    }

    /// <summary>Returns <c>true</c> when <paramref name="level"/> is a defined enum value.</summary>
    /// <param name="level">Candidate conformance level.</param>
    /// <returns><c>true</c> iff acceptable.</returns>
    internal static bool IsKnownConformance(PdfAConformanceLevel level)
        => Enum.IsDefined(level);
}
