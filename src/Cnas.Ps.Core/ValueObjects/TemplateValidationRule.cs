using System;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Core.ValueObjects;

/// <summary>
/// R0131 / CF 17.15 — kind of validation rule applied to a single template form field.
/// Stable upper-case identifiers so the JSON shape stored in
/// <c>DocumentTemplate.ValidationRulesJson</c> remains a public-contract surface.
/// </summary>
/// <remarks>
/// New kinds may be appended at the end of the enum without breaking back-compat;
/// renaming or renumbering is a breaking change because the persisted JSON encodes
/// the string name verbatim.
/// </remarks>
public enum TemplateValidationRuleKind
{
    /// <summary>The field must be present and non-blank (after trim).</summary>
    Required = 0,

    /// <summary>
    /// The field's length must NOT exceed the integer value carried by
    /// <see cref="TemplateValidationRule.Argument"/>.
    /// </summary>
    MaxLength = 1,

    /// <summary>
    /// The field's length must be at LEAST the integer value carried by
    /// <see cref="TemplateValidationRule.Argument"/>.
    /// </summary>
    MinLength = 2,

    /// <summary>
    /// The field's value must match the .NET regular-expression pattern carried by
    /// <see cref="TemplateValidationRule.Argument"/> (compiled with
    /// <c>RegexOptions.CultureInvariant</c>).
    /// </summary>
    Regex = 3,

    /// <summary>
    /// The field's value must parse to a <see cref="decimal"/> AND fall within the
    /// inclusive range <c>"min..max"</c> carried by
    /// <see cref="TemplateValidationRule.Argument"/> (e.g. <c>"0..100"</c>).
    /// </summary>
    Range = 4,

    /// <summary>
    /// Reserved for caller-supplied validation logic (e.g. cross-field invariants the
    /// metadata-driven service cannot express). The metadata service ignores rules of
    /// this kind so a passport configuration that lists <c>Custom</c> rules does not
    /// silently fail-open on the wire.
    /// </summary>
    Custom = 5,
}

/// <summary>
/// R0131 / CF 17.15 — declarative per-template validation rule. Each rule binds a single
/// form-field name to a <see cref="TemplateValidationRuleKind"/> plus an optional
/// <see cref="Argument"/> whose shape depends on the kind:
/// <list type="bullet">
///   <item><c>Required</c> — argument ignored.</item>
///   <item><c>MaxLength</c> / <c>MinLength</c> — integer string (e.g. <c>"128"</c>).</item>
///   <item><c>Regex</c> — .NET regex pattern (e.g. <c>"^[A-Z]{3}$"</c>).</item>
///   <item><c>Range</c> — <c>"min..max"</c> decimal range (e.g. <c>"0..100"</c>).</item>
///   <item><c>Custom</c> — opaque (the metadata service ignores these).</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// <b>Validation contract.</b> Instances are produced exclusively through
/// <see cref="Create"/>, returning a <see cref="Result{T}"/>. The constructor is
/// private — there is no path to an invalid instance (e.g. an empty field name).
/// </para>
/// <para>
/// <b>Persistence shape.</b> The full rule-set lives on
/// <c>DocumentTemplate.ValidationRulesJson</c> as a JSON array of
/// <c>{ "fieldName": "...", "ruleKind": "Required|MaxLength|...", "argument": "..." }</c>
/// objects. The decoding lives in
/// <c>Cnas.Ps.Application.Templates.ITemplateValidationService</c>.
/// </para>
/// </remarks>
public sealed class TemplateValidationRule
{
    private TemplateValidationRule(string fieldName, TemplateValidationRuleKind ruleKind, string? argument)
    {
        FieldName = fieldName;
        RuleKind = ruleKind;
        Argument = argument;
    }

    /// <summary>
    /// Name of the form field the rule applies to — matched case-sensitively against the
    /// key of the form-values dictionary supplied to the validator.
    /// </summary>
    public string FieldName { get; }

    /// <summary>The rule discriminator.</summary>
    public TemplateValidationRuleKind RuleKind { get; }

    /// <summary>
    /// Free-form argument whose interpretation depends on <see cref="RuleKind"/>. May
    /// be <see langword="null"/> for kinds (e.g. <see cref="TemplateValidationRuleKind.Required"/>)
    /// that need no parameter.
    /// </summary>
    public string? Argument { get; }

    /// <summary>
    /// Builds a validated <see cref="TemplateValidationRule"/> or returns a failure carrying
    /// <see cref="ErrorCodes.ValidationFailed"/>. The argument shape is validated against
    /// the supplied kind:
    /// <list type="bullet">
    ///   <item><c>MaxLength</c> / <c>MinLength</c> — argument must parse to a non-negative int.</item>
    ///   <item><c>Regex</c> — argument must be a non-empty string (the regex itself is not
    ///   pre-compiled here — invalid patterns surface at validation time as a per-call failure).</item>
    ///   <item><c>Range</c> — argument must match <c>"min..max"</c> with two decimal-parsable halves and min &lt;= max.</item>
    ///   <item><c>Required</c> / <c>Custom</c> — argument is ignored.</item>
    /// </list>
    /// </summary>
    /// <param name="fieldName">Non-blank field name.</param>
    /// <param name="ruleKind">Rule discriminator.</param>
    /// <param name="argument">Argument whose shape depends on <paramref name="ruleKind"/>; may be null for argument-less kinds.</param>
    /// <returns>Success with the constructed value, or a validation failure.</returns>
    public static Result<TemplateValidationRule> Create(
        string fieldName,
        TemplateValidationRuleKind ruleKind,
        string? argument)
    {
        if (string.IsNullOrWhiteSpace(fieldName))
        {
            return Result<TemplateValidationRule>.Failure(
                ErrorCodes.ValidationFailed,
                "FieldName must be non-blank.");
        }

        switch (ruleKind)
        {
            case TemplateValidationRuleKind.MaxLength:
            case TemplateValidationRuleKind.MinLength:
                if (!int.TryParse(argument, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out var len) || len < 0)
                {
                    return Result<TemplateValidationRule>.Failure(
                        ErrorCodes.ValidationFailed,
                        $"Argument for {ruleKind} must be a non-negative integer.");
                }
                break;

            case TemplateValidationRuleKind.Regex:
                if (string.IsNullOrEmpty(argument))
                {
                    return Result<TemplateValidationRule>.Failure(
                        ErrorCodes.ValidationFailed,
                        "Argument for Regex must be a non-empty pattern.");
                }
                break;

            case TemplateValidationRuleKind.Range:
                if (string.IsNullOrEmpty(argument)
                    || !TryParseRange(argument, out _, out _))
                {
                    return Result<TemplateValidationRule>.Failure(
                        ErrorCodes.ValidationFailed,
                        "Argument for Range must be \"min..max\" with min <= max.");
                }
                break;

            case TemplateValidationRuleKind.Required:
            case TemplateValidationRuleKind.Custom:
            default:
                // No argument validation required.
                break;
        }

        return Result<TemplateValidationRule>.Success(
            new TemplateValidationRule(fieldName.Trim(), ruleKind, argument));
    }

    /// <summary>
    /// Parses the <c>"min..max"</c> Range argument form used by
    /// <see cref="TemplateValidationRuleKind.Range"/>. Returns false when either half
    /// fails to parse as decimal in invariant culture, or when <c>max &lt; min</c>.
    /// </summary>
    /// <param name="argument">The argument string (e.g. <c>"0..100"</c>).</param>
    /// <param name="min">Parsed lower bound on success.</param>
    /// <param name="max">Parsed upper bound on success.</param>
    /// <returns><see langword="true"/> when both halves parse and min &lt;= max.</returns>
    public static bool TryParseRange(string argument, out decimal min, out decimal max)
    {
        min = default;
        max = default;
        if (string.IsNullOrEmpty(argument))
        {
            return false;
        }

        var idx = argument.IndexOf("..", StringComparison.Ordinal);
        if (idx <= 0 || idx >= argument.Length - 2)
        {
            return false;
        }

        var minPart = argument[..idx];
        var maxPart = argument[(idx + 2)..];
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var ns = System.Globalization.NumberStyles.Number;
        if (!decimal.TryParse(minPart, ns, inv, out min)
            || !decimal.TryParse(maxPart, ns, inv, out max))
        {
            return false;
        }

        return max >= min;
    }
}
