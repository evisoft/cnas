using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Core.ValueObjects;

/// <summary>
/// R0122 / TOR CF 16.07 — strongly-typed performer descriptor for a workflow step.
/// Replaces the prior ad-hoc JSON shape (free-form <c>{"kind":"role","code":"..."}</c>)
/// with an immutable, self-validating value object that the Application layer can
/// reason about, validate, and round-trip through DTOs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Validation contract.</b> Instances are produced exclusively through
/// <see cref="Create"/>, which returns a <see cref="Result{T}"/> per the project's
/// Result-pattern convention. The constructor itself is private — there is no path to
/// an invalid instance. This mirrors the value-object discipline applied to
/// <see cref="ValueObjects.Money"/>, <see cref="ValueObjects.Idnp"/>, etc.
/// </para>
/// <para>
/// <b>Fallback semantics.</b> When the primary <see cref="Kind"/> cannot find an
/// eligible assignee at dispatch time, the workflow engine consults the optional
/// <see cref="FallbackKind"/> / <see cref="FallbackCode"/> tuple. Both fallback fields
/// MUST be supplied together; supplying one without the other is rejected so a
/// partial spec cannot silently lose the fallback edge.
/// </para>
/// <para>
/// <b>Code length.</b> Capped at 64 characters to match the rest of the codebase's
/// role / group code conventions (<see cref="UserProfile.Roles"/> entries, the
/// <see cref="WorkflowStepAcl"/> validator).
/// </para>
/// </remarks>
public sealed class WorkflowPerformerAssignment
{
    /// <summary>Maximum length for a performer code (role / group / NamedUser sqid).</summary>
    public const int MaxCodeLength = 64;

    private WorkflowPerformerAssignment(
        WorkflowPerformerKind kind,
        string? code,
        WorkflowPerformerKind? fallbackKind,
        string? fallbackCode)
    {
        Kind = kind;
        Code = code;
        FallbackKind = fallbackKind;
        FallbackCode = fallbackCode;
    }

    /// <summary>The primary performer kind for this step.</summary>
    public WorkflowPerformerKind Kind { get; }

    /// <summary>
    /// The code that pairs with <see cref="Kind"/>. Required for
    /// <see cref="WorkflowPerformerKind.Role"/>, <see cref="WorkflowPerformerKind.Group"/>,
    /// and <see cref="WorkflowPerformerKind.NamedUser"/>; null / unused for
    /// <see cref="WorkflowPerformerKind.Originator"/> and
    /// <see cref="WorkflowPerformerKind.Supervisor"/> which are reflexive.
    /// </summary>
    public string? Code { get; }

    /// <summary>Optional fallback kind consulted when the primary path yields no
    /// eligible assignee.</summary>
    public WorkflowPerformerKind? FallbackKind { get; }

    /// <summary>The fallback code paired with <see cref="FallbackKind"/>. Same shape
    /// rules as <see cref="Code"/>.</summary>
    public string? FallbackCode { get; }

    /// <summary>
    /// Builds a validated <see cref="WorkflowPerformerAssignment"/> or returns a
    /// failure carrying <see cref="ErrorCodes.ValidationFailed"/>.
    /// </summary>
    /// <param name="kind">Primary performer kind.</param>
    /// <param name="code">Primary code (role / group / Sqid); may be null for
    /// reflexive kinds.</param>
    /// <param name="fallbackKind">Optional fallback kind. Pair with
    /// <paramref name="fallbackCode"/>.</param>
    /// <param name="fallbackCode">Optional fallback code. Pair with
    /// <paramref name="fallbackKind"/>.</param>
    /// <returns>Success with the constructed value, or
    /// <see cref="ErrorCodes.ValidationFailed"/> describing the shape violation.</returns>
    public static Result<WorkflowPerformerAssignment> Create(
        WorkflowPerformerKind kind,
        string? code,
        WorkflowPerformerKind? fallbackKind = null,
        string? fallbackCode = null)
    {
        var primary = ValidateCode(kind, code, slot: "Code");
        if (primary.IsFailure)
        {
            return Result<WorkflowPerformerAssignment>.From(primary);
        }

        // Partial fallback specifications are rejected to surface configuration bugs
        // at edit time rather than at dispatch time.
        if (fallbackKind.HasValue ^ !string.IsNullOrWhiteSpace(fallbackCode))
        {
            // XOR — exactly one half supplied (the kind without code OR code without
            // kind for non-reflexive kinds). The reflexive kinds (Originator /
            // Supervisor) tolerate a missing code, so re-evaluate via ValidateCode
            // when the kind IS present.
            if (fallbackKind.HasValue && !string.IsNullOrWhiteSpace(fallbackCode))
            {
                // unreachable — kept for symmetry
            }
            else if (fallbackKind.HasValue && string.IsNullOrWhiteSpace(fallbackCode))
            {
                var fallback = ValidateCode(fallbackKind.Value, fallbackCode, slot: "FallbackCode");
                if (fallback.IsFailure)
                {
                    return Result<WorkflowPerformerAssignment>.From(fallback);
                }
            }
            else
            {
                return Result<WorkflowPerformerAssignment>.Failure(
                    ErrorCodes.ValidationFailed,
                    "FallbackCode supplied without FallbackKind.");
            }
        }
        else if (fallbackKind.HasValue)
        {
            var fallback = ValidateCode(fallbackKind.Value, fallbackCode, slot: "FallbackCode");
            if (fallback.IsFailure)
            {
                return Result<WorkflowPerformerAssignment>.From(fallback);
            }
        }

        return Result<WorkflowPerformerAssignment>.Success(
            new WorkflowPerformerAssignment(kind, code, fallbackKind, fallbackCode));
    }

    /// <summary>
    /// Validates a (kind, code) pair: requires a non-empty 1..64-char code for
    /// non-reflexive kinds; tolerates a null/empty code for
    /// <see cref="WorkflowPerformerKind.Originator"/> /
    /// <see cref="WorkflowPerformerKind.Supervisor"/>.
    /// </summary>
    /// <param name="kind">Kind to validate against.</param>
    /// <param name="code">Code value (may be null/empty).</param>
    /// <param name="slot">Field name to embed in any failure message.</param>
    /// <returns>Success on a legal pair, otherwise a validation failure.</returns>
    private static Result ValidateCode(WorkflowPerformerKind kind, string? code, string slot)
    {
        var isReflexive = kind is WorkflowPerformerKind.Originator or WorkflowPerformerKind.Supervisor;
        if (isReflexive)
        {
            // Reflexive kinds carry no code; ignore any supplied value.
            return Result.Success();
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            return Result.Failure(
                ErrorCodes.ValidationFailed,
                $"{slot} is required when Kind = {kind}.");
        }

        if (code.Length > MaxCodeLength)
        {
            return Result.Failure(
                ErrorCodes.ValidationFailed,
                $"{slot} exceeds the {MaxCodeLength}-character cap.");
        }

        return Result.Success();
    }
}
