using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R0673 / TOR CF 18.12 — FluentValidation rules for
/// <see cref="GranularPermissionAssignInput"/>. Pins the shape of an admin
/// "grant permission to role" request at the application boundary so the
/// controller can reject malformed payloads before reaching the persistence
/// layer.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this validates.</b> Three stable strings that compose the natural
/// key of a granular permission row: <c>RoleCode</c> (the recipient role),
/// <c>ResourceType</c> (the PascalCase resource discriminator, e.g.
/// <c>"Dossier"</c>), and <c>PermissionVerb</c> (one of the canonical verbs
/// declared in <see cref="PermissionVerbs.All"/>). The verb gate uses the
/// stable allow-list so a typo cannot slip through and pollute the matrix.
/// </para>
/// <para>
/// <b>What this deliberately does NOT validate.</b> The <c>RoleCode</c> is
/// not gated against <see cref="RoleCodes"/> here — the granular-permission
/// service performs its own role-existence check and returns
/// <see cref="ErrorCodes.GranularPermissionUnknownRole"/> with a richer
/// message. Length / presence is enforced at this layer; semantic validity
/// is the service's responsibility.
/// </para>
/// <para>
/// <b>Registration.</b> Auto-registered through
/// <c>AddValidatorsFromAssemblyContaining&lt;ApplicationAssemblyMarker&gt;</c>
/// in <c>ApplicationServiceCollectionExtensions</c>. Controllers that need it
/// inject <c>IValidator&lt;GranularPermissionAssignInput&gt;</c>.
/// </para>
/// </remarks>
public sealed class GranularPermissionAssignInputValidator
    : AbstractValidator<GranularPermissionAssignInput>
{
    /// <summary>Maximum length of the role code (mirrors the EF column cap).</summary>
    public const int MaxRoleCodeLength = 64;

    /// <summary>Maximum length of the resource discriminator (mirrors the EF column cap).</summary>
    public const int MaxResourceTypeLength = 64;

    /// <summary>Constructs the validator with the documented rule set.</summary>
    public GranularPermissionAssignInputValidator()
    {
        RuleFor(x => x.RoleCode)
            .NotEmpty().WithMessage("RoleCode is required.")
            .MaximumLength(MaxRoleCodeLength)
            .WithMessage($"RoleCode must be ≤ {MaxRoleCodeLength} characters.");

        RuleFor(x => x.ResourceType)
            .NotEmpty().WithMessage("ResourceType is required.")
            .MaximumLength(MaxResourceTypeLength)
            .WithMessage($"ResourceType must be ≤ {MaxResourceTypeLength} characters.");

        RuleFor(x => x.PermissionVerb)
            .NotEmpty().WithMessage("PermissionVerb is required.")
            .Must(v => v is not null && PermissionVerbs.All.Contains(v))
            .WithMessage(
                "PermissionVerb must be one of: View/Add/Modify/StatusChange/Generate/Download.");
    }
}
