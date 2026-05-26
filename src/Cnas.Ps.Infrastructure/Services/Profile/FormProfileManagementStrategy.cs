using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Profile;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Infrastructure.Services.Profile;

/// <summary>
/// R0622 / TOR CF 13.03 — form-intake strategy. Routes a paper /
/// front-desk form submission through the iter-128
/// <see cref="IFormIntakeService"/> validator and then translates the
/// payload into a profile contact update via
/// <see cref="IProfileService.UpdateMyContactAsync(ProfileContactInput, CancellationToken)"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two-stage validation.</b> First the JSON payload is validated
/// against the addressed ServicePassport's form schema (CLAUDE.md "validate
/// at boundaries"). Only after that pass do we extract the profile-update
/// fields and apply them through the existing UI path — that way the
/// front-desk channel re-uses the same audit + persistence guarantees as
/// the self-service channel.
/// </para>
/// <para>
/// <b>Required keys.</b> The form payload MUST carry at least the
/// <c>displayName</c> string. Optional <c>email</c> + <c>phone</c> keys
/// are surfaced when present. A missing <c>displayName</c> surfaces as
/// <see cref="ErrorCodes.ProfileFormIntakePayloadInvalid"/> so dashboards
/// can attribute "intake payload structurally OK but missing profile-update
/// keys" as a separate signal from a structurally-broken form.
/// </para>
/// </remarks>
/// <param name="formIntake">Form-intake schema validator (iter 128).</param>
/// <param name="profiles">Underlying profile service.</param>
public sealed class FormProfileManagementStrategy(
    IFormIntakeService formIntake,
    IProfileService profiles)
    : IProfileManagementStrategy
{
    private readonly IFormIntakeService _formIntake = formIntake;
    private readonly IProfileService _profiles = profiles;

    /// <inheritdoc />
    public string StrategyKey => ProfileManagementStrategyKeys.Form;

    /// <inheritdoc />
    public async Task<Result<ProfileOutput>> ApplyAsync(
        string profileSqid,
        ProfileManagementInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (string.IsNullOrWhiteSpace(input.ServicePassportSqid))
        {
            return Result<ProfileOutput>.Failure(
                ErrorCodes.ValidationFailed,
                "ServicePassportSqid is required for the Form management strategy.");
        }
        if (string.IsNullOrWhiteSpace(input.FormPayloadJson))
        {
            return Result<ProfileOutput>.Failure(
                ErrorCodes.ValidationFailed,
                "FormPayloadJson is required for the Form management strategy.");
        }

        // Stage 1 — schema validation through the iter-128 intake pipeline.
        var validation = await _formIntake
            .ValidateAsync(input.ServicePassportSqid, input.FormPayloadJson, cancellationToken)
            .ConfigureAwait(false);
        if (validation.IsFailure)
        {
            return Result<ProfileOutput>.Failure(validation.ErrorCode!, validation.ErrorMessage!);
        }

        // Stage 2 — extract the profile-update keys from the now-validated payload.
        string? displayName;
        string? email;
        string? phone;
        try
        {
            using var doc = JsonDocument.Parse(input.FormPayloadJson);
            var root = doc.RootElement;
            displayName = TryGetString(root, "displayName") ?? input.DisplayName;
            email = TryGetString(root, "email") ?? input.Email;
            phone = TryGetString(root, "phone") ?? input.Phone;
        }
        catch (JsonException ex)
        {
            // Should be unreachable — Stage 1 already parsed the payload — but kept as
            // defense-in-depth so a stage-2 parse anomaly never crashes the dispatcher.
            return Result<ProfileOutput>.Failure(ErrorCodes.ValidationFailed, ex.Message);
        }

        if (string.IsNullOrWhiteSpace(displayName))
        {
            return Result<ProfileOutput>.Failure(
                ErrorCodes.ProfileFormIntakePayloadInvalid,
                "Form payload must declare a non-empty 'displayName' string.");
        }

        var contactInput = new ProfileContactInput(
            DisplayName: displayName,
            Email: email,
            Phone: phone);
        var update = await _profiles
            .UpdateMyContactAsync(contactInput, cancellationToken)
            .ConfigureAwait(false);
        if (update.IsFailure)
        {
            return Result<ProfileOutput>.Failure(update.ErrorCode!, update.ErrorMessage!);
        }

        return await _profiles.GetMineAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the string-typed property <paramref name="name"/> from
    /// <paramref name="root"/>, or <c>null</c> when the property is absent
    /// or not a JSON string.
    /// </summary>
    /// <param name="root">Parsed payload root element.</param>
    /// <param name="name">Property name to lookup (case-sensitive — JSON keys are case-sensitive by spec).</param>
    /// <returns>The string value, or <c>null</c>.</returns>
    private static string? TryGetString(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (!root.TryGetProperty(name, out var p)) return null;
        return p.ValueKind == JsonValueKind.String ? p.GetString() : null;
    }
}
