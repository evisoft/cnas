using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

/// <summary>
/// R0622 / TOR CF 13.03 — neutral carrier passed through the
/// profile-management strategy dispatcher. The shape is intentionally
/// channel-agnostic: each strategy implementation consumes only the slice
/// it needs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Per-channel field semantics.</b>
/// <list type="bullet">
///   <item>
///     <description>
///       <c>Ui</c> strategy — consumes
///       <see cref="DisplayName"/>, <see cref="Email"/>, <see cref="Phone"/>
///       directly (mirrors the existing
///       <see cref="ProfileContactInput"/> shape consumed by
///       <c>PUT /api/profile/contact</c>). Identity fields and
///       <see cref="ExternalSync"/> are ignored.
///     </description>
///   </item>
///   <item>
///     <description>
///       <c>Form</c> strategy — consumes
///       <see cref="ServicePassportSqid"/> + <see cref="FormPayloadJson"/>
///       and pushes those through <c>IFormIntakeService</c> style
///       validation before translating to a profile contact update. The
///       form payload MUST carry at least the <c>displayName</c> key;
///       optional <c>email</c> + <c>phone</c> keys are surfaced when
///       present.
///     </description>
///   </item>
///   <item>
///     <description>
///       <c>ExternalSync</c> strategy — consumes
///       <see cref="ExternalSync"/>. The stub implementation returns
///       <c>PROFILE.EXTERNAL_SYNC_NOT_CONFIGURED</c> until the MConnect
///       transport is in place (externally gated per
///       <c>EGOV-INTEGRATION-GAP §MConnect</c>).
///     </description>
///   </item>
/// </list>
/// </para>
/// <para>
/// <b>Mass-assignment safety.</b> The DTO deliberately excludes
/// <c>Id</c>, <c>Roles</c>, <c>IsActive</c>, and <c>CreatedAtUtc</c>
/// (CLAUDE.md §2.4). The identity fields are owned by the lifecycle
/// endpoints, not the profile-management surface.
/// </para>
/// </remarks>
/// <param name="DisplayName">
/// Citizen's human-readable display name. Consumed by the <c>Ui</c>
/// strategy directly; the <c>Form</c> strategy reads it from
/// <see cref="FormPayloadJson"/> when supplied; the <c>ExternalSync</c>
/// strategy reads it from <see cref="ExternalSync"/>.
/// </param>
/// <param name="Email">
/// Optional contact e-mail. <c>null</c> clears the persisted value
/// (PUT semantics).
/// </param>
/// <param name="Phone">
/// Optional E.164 phone. <c>null</c> clears the persisted value.
/// </param>
/// <param name="ServicePassportSqid">
/// Form-intake passport id. Required for the <c>Form</c> strategy;
/// ignored by <c>Ui</c> / <c>ExternalSync</c>.
/// </param>
/// <param name="FormPayloadJson">
/// Raw form-intake payload. Required for the <c>Form</c> strategy.
/// </param>
/// <param name="ExternalSync">
/// External-sync envelope. Required for the <c>ExternalSync</c> strategy.
/// </param>
public sealed record ProfileManagementInput(
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    string? DisplayName = null,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    string? Email = null,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    string? Phone = null,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string? ServicePassportSqid = null,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    string? FormPayloadJson = null,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    ExternalProfileSyncInput? ExternalSync = null);

/// <summary>
/// R0622 / TOR CF 13.03 — external-sync envelope carried by
/// <see cref="ProfileManagementInput.ExternalSync"/>. The
/// <c>ExternalSyncProfileManagementStrategy</c> reads the source-system
/// discriminator and the opaque payload, then dispatches to the matching
/// authoritative-source adapter (MConnect / RSP).
/// </summary>
/// <remarks>
/// <para>
/// <b>Externally gated.</b> The default implementation returns
/// <c>PROFILE.EXTERNAL_SYNC_NOT_CONFIGURED</c> until the MConnect
/// transport is in place (per <c>EGOV-INTEGRATION-GAP §MConnect</c>).
/// The envelope ships now so the wire shape is stable and downstream
/// integrators can target the same DTO once the transport lands.
/// </para>
/// </remarks>
/// <param name="SourceSystem">
/// Authoritative-source discriminator (e.g. <c>MConnect</c>, <c>RSP</c>,
/// <c>RSUD</c>). Case-insensitive; the strategy normalises before
/// dispatching.
/// </param>
/// <param name="Payload">
/// Opaque source-specific payload (typically the verbatim MConnect
/// envelope). The strategy is responsible for parsing.
/// </param>
public sealed record ExternalProfileSyncInput(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string SourceSystem,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    string Payload);
