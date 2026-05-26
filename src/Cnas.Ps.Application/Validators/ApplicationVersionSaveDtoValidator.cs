using System.Text;
using System.Text.Json;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Domain;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R0321 / R0224 / UI 008 — FluentValidation rules for
/// <see cref="ApplicationVersionSaveDto"/>. Enforces the three caps documented on the
/// DTO: non-empty + syntactically-valid JSON ≤ 500 KB on <c>FormDataJson</c>, a known
/// enum value on <c>Source</c>, and ≤ 1000 chars on the optional <c>Note</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Size cap.</b> The 500 KB ceiling is measured against the UTF-8 byte length
/// (matching the wire form). A larger payload almost certainly indicates either a UI
/// bug serialising the whole document tree or a deliberate abuse attempt — the
/// autosave subsystem is not a freeform storage area.
/// </para>
/// <para>
/// <b>JSON validity.</b> The validator try-parses the payload with
/// <see cref="JsonDocument.Parse(string, JsonDocumentOptions)"/> so a malformed
/// payload is rejected at the API boundary rather than silently persisted and
/// failing later at deserialise time on revert. The parsed document is discarded —
/// the storage layer round-trips the original bytes verbatim.
/// </para>
/// </remarks>
public sealed class ApplicationVersionSaveDtoValidator : AbstractValidator<ApplicationVersionSaveDto>
{
    /// <summary>Maximum permitted UTF-8 byte length of <c>FormDataJson</c>: 500 KB.</summary>
    public const int MaxFormDataBytes = 500 * 1024;

    /// <summary>Maximum permitted character length of <c>Note</c>: 1000 chars.</summary>
    public const int MaxNoteLength = 1000;

    /// <summary>Creates the validator with the full rule set.</summary>
    public ApplicationVersionSaveDtoValidator()
    {
        // Split into two rules so the .When() scoping doesn't accidentally short-circuit
        // the NotEmpty check (FluentValidation's .When applies to every preceding rule in
        // the same chain — an empty payload would otherwise skip the entire chain).
        RuleFor(x => x.FormDataJson)
            .NotEmpty()
            .WithMessage("FormDataJson is required.");

        RuleFor(x => x.FormDataJson)
            .Must(BeWithinByteCap)
            .WithMessage($"FormDataJson exceeds the {MaxFormDataBytes / 1024} KB cap.")
            .Must(BeWellFormedJson!)
            .WithMessage("FormDataJson must be syntactically valid JSON.")
            .When(x => !string.IsNullOrEmpty(x.FormDataJson));

        RuleFor(x => x.Source)
            .NotEmpty()
            .WithMessage("Source is required.");

        RuleFor(x => x.Source)
            .Must(BeKnownSource!)
            .WithMessage("Source must be one of Autosave / ManualSave / Submit / Revert.")
            .When(x => !string.IsNullOrEmpty(x.Source));

        RuleFor(x => x.Note)
            .MaximumLength(MaxNoteLength)
            .WithMessage($"Note exceeds the {MaxNoteLength}-character cap.")
            .When(x => !string.IsNullOrEmpty(x.Note));
    }

    /// <summary>
    /// Returns <c>true</c> when the UTF-8 byte length of <paramref name="payload"/>
    /// does not exceed <see cref="MaxFormDataBytes"/>. Operates on bytes (not chars)
    /// because the wire form is UTF-8 — a payload of mostly multi-byte characters
    /// would slip past a character-count check.
    /// </summary>
    /// <param name="payload">Candidate payload string.</param>
    /// <returns><c>true</c> when within the cap; <c>false</c> otherwise.</returns>
    private static bool BeWithinByteCap(string? payload)
    {
        if (string.IsNullOrEmpty(payload)) return true;
        return Encoding.UTF8.GetByteCount(payload) <= MaxFormDataBytes;
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="payload"/> parses as a syntactically
    /// valid JSON document. The parsed <see cref="JsonDocument"/> is disposed
    /// immediately — only the well-formedness signal is consumed.
    /// </summary>
    /// <param name="payload">Candidate JSON payload.</param>
    /// <returns><c>true</c> on a well-formed parse; <c>false</c> otherwise.</returns>
    private static bool BeWellFormedJson(string payload)
    {
        try
        {
            using var _ = JsonDocument.Parse(payload);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="candidate"/> case-sensitively matches a
    /// defined <see cref="ApplicationVersionSource"/> value. Case-sensitive on purpose:
    /// the wire contract is PascalCase, and accepting lowercase variants would let
    /// drift accumulate.
    /// </summary>
    /// <param name="candidate">Candidate source string.</param>
    /// <returns><c>true</c> when the string maps to a known enum value.</returns>
    private static bool BeKnownSource(string candidate)
        => Enum.TryParse<ApplicationVersionSource>(candidate, ignoreCase: false, out _);
}
