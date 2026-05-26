using Cnas.Ps.Contracts;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R0583 / TOR CF 09.06 / CF 09.09 — FluentValidation rules for
/// <see cref="ReportJobEnqueueDto"/>. Defends the
/// <c>POST /api/reports/jobs</c> endpoint at the wire boundary BEFORE the
/// service is invoked: requires a non-empty template Sqid (decode happens
/// server-side via <c>ISqidService</c>) and a parseable
/// <see cref="ExportFormat"/> name.
/// </summary>
/// <remarks>
/// The decode-success of the template Sqid is intentionally NOT enforced here
/// because the validator does not have access to <c>ISqidService</c>. The
/// service layer surfaces an <c>INVALID_SQID</c> failure instead.
/// </remarks>
public sealed class ReportJobEnqueueDtoValidator : AbstractValidator<ReportJobEnqueueDto>
{
    /// <summary>Builds the validator with the full rule set.</summary>
    public ReportJobEnqueueDtoValidator()
    {
        RuleFor(x => x.ReportTemplateSqid)
            .NotEmpty().WithMessage("ReportTemplateSqid is required.");

        RuleFor(x => x.Format)
            .NotEmpty().WithMessage("Format is required.")
            .Must(BeKnownFormat!)
            .When(x => !string.IsNullOrEmpty(x.Format))
            .WithMessage("Format must be one of Csv / Xlsx / Pdf.");
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="candidate"/> parses to an
    /// <see cref="ExportFormat"/> enum value (case-sensitive).
    /// </summary>
    /// <param name="candidate">Candidate format string.</param>
    /// <returns><c>true</c> when known.</returns>
    private static bool BeKnownFormat(string candidate)
        => Enum.TryParse<ExportFormat>(candidate, ignoreCase: false, out _);
}
