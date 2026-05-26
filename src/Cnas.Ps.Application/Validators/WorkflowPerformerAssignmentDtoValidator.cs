using System;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Core.ValueObjects;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R0122 / TOR CF 16.07 — validator for <see cref="WorkflowPerformerAssignmentDto"/>.
/// Gates the shape of a performer descriptor before any handler / engine touches it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Validation surface.</b>
/// <list type="bullet">
///   <item><c>Kind</c> must parse to a member of
///   <see cref="WorkflowPerformerKind"/>;</item>
///   <item><c>Kind = Role</c> requires <c>Code</c> to match a known
///   <see cref="RoleCodes"/> entry (case-sensitive);</item>
///   <item><c>Kind = Group</c> requires <c>Code</c> to be non-empty and ≤ 64 chars
///   (no registry check today — the runtime resolver enforces existence);</item>
///   <item><c>Kind = NamedUser</c> requires <c>Code</c> to decode through
///   <see cref="ISqidService"/>;</item>
///   <item>reflexive kinds (Originator / Supervisor) tolerate a null
///   <c>Code</c>.</item>
/// </list>
/// </para>
/// <para>
/// <b>Sqid surface.</b> The decoder is injected so this validator stays composable
/// in tests; production DI wires the same singleton used by every other Sqid
/// boundary.
/// </para>
/// </remarks>
public sealed class WorkflowPerformerAssignmentDtoValidator : AbstractValidator<WorkflowPerformerAssignmentDto>
{
    /// <summary>Creates the validator with its Sqid collaborator.</summary>
    /// <param name="sqids">Sqid encoder/decoder used to validate
    /// <see cref="WorkflowPerformerKind.NamedUser"/> codes.</param>
    public WorkflowPerformerAssignmentDtoValidator(ISqidService sqids)
    {
        ArgumentNullException.ThrowIfNull(sqids);

        RuleFor(x => x.Kind)
            .NotEmpty().WithMessage("Kind is required.")
            .Must(KindIsKnown)
            .WithMessage("Kind must be one of: Role, Group, NamedUser, Originator, Supervisor.");

        RuleFor(x => x.Code)
            .Must((dto, code) => CodeShapeMatchesKind(dto.Kind, code, sqids, out _))
            .When(x => KindIsKnown(x.Kind))
            .WithMessage(dto =>
            {
                _ = CodeShapeMatchesKind(dto.Kind, dto.Code, sqids, out var reason);
                return reason ?? "Code shape does not match the supplied Kind.";
            });

        RuleFor(x => x.FallbackKind)
            .Must(k => k is null || KindIsKnown(k))
            .WithMessage("FallbackKind must be one of: Role, Group, NamedUser, Originator, Supervisor.");

        RuleFor(x => x.FallbackCode)
            .Must((dto, code) => string.IsNullOrEmpty(dto.FallbackKind)
                || CodeShapeMatchesKind(dto.FallbackKind, code, sqids, out _))
            .WithMessage(dto =>
            {
                _ = CodeShapeMatchesKind(dto.FallbackKind, dto.FallbackCode, sqids, out var reason);
                return reason ?? "FallbackCode shape does not match the supplied FallbackKind.";
            });
    }

    /// <summary>True when <paramref name="kind"/> parses to a known
    /// <see cref="WorkflowPerformerKind"/>.</summary>
    /// <param name="kind">String form of the kind (e.g. "Role").</param>
    /// <returns><c>true</c> on a known kind, otherwise <c>false</c>.</returns>
    private static bool KindIsKnown(string? kind) =>
        !string.IsNullOrWhiteSpace(kind)
        && Enum.TryParse<WorkflowPerformerKind>(kind, ignoreCase: false, out _);

    /// <summary>
    /// Validates the (kind, code) pair against the kind-specific rules and emits a
    /// human-readable reason string when the pair is rejected:
    /// Role → in <see cref="RoleCodes.All"/>; Group → 1..64-char non-empty string;
    /// NamedUser → Sqid that decodes; reflexive → tolerate null.
    /// </summary>
    /// <param name="kindString">String form of the kind.</param>
    /// <param name="code">Code value supplied by the caller.</param>
    /// <param name="sqids">Sqid decoder for the NamedUser branch.</param>
    /// <param name="reason">Set to a kind-specific human message on rejection.</param>
    /// <returns><c>true</c> when the pair is well-formed for the kind.</returns>
    private static bool CodeShapeMatchesKind(
        string? kindString,
        string? code,
        ISqidService sqids,
        out string? reason)
    {
        reason = null;
        if (!Enum.TryParse<WorkflowPerformerKind>(kindString, ignoreCase: false, out var kind))
        {
            // KindIsKnown gate prevents this branch in practice; defence in depth.
            reason = "Kind did not parse.";
            return false;
        }

        switch (kind)
        {
            case WorkflowPerformerKind.Role:
                if (string.IsNullOrWhiteSpace(code))
                {
                    reason = "Code is required for Role assignments.";
                    return false;
                }
                if (!RoleCodes.All.Contains(code))
                {
                    reason = $"Unknown Role code '{code}'.";
                    return false;
                }
                return true;

            case WorkflowPerformerKind.Group:
                if (string.IsNullOrWhiteSpace(code))
                {
                    reason = "Code is required for Group assignments.";
                    return false;
                }
                if (code.Length > WorkflowPerformerAssignment.MaxCodeLength)
                {
                    reason = $"Group code exceeds the {WorkflowPerformerAssignment.MaxCodeLength}-character cap.";
                    return false;
                }
                return true;

            case WorkflowPerformerKind.NamedUser:
                if (string.IsNullOrWhiteSpace(code))
                {
                    reason = "Code (Sqid) is required for NamedUser assignments.";
                    return false;
                }
                if (sqids.TryDecode(code).IsFailure)
                {
                    reason = "Code must be a valid Sqid for NamedUser assignments.";
                    return false;
                }
                return true;

            case WorkflowPerformerKind.Originator:
            case WorkflowPerformerKind.Supervisor:
                // Reflexive — code is ignored.
                return true;

            default:
                reason = "Kind did not match any known performer kind.";
                return false;
        }
    }
}
