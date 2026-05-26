using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

/// <summary>
/// Compact user-listing row (UC18). The <paramref name="State"/> field is the canonical
/// lifecycle signal per R0059 / SEC 016 — the prior <c>IsLocked</c> boolean has been
/// replaced because the four-state machine (Active / Suspended / Disabled / Locked)
/// cannot be projected into a single boolean.
/// </summary>
/// <param name="Id">Sqid-encoded user id (CLAUDE.md RULE 3).</param>
/// <param name="DisplayName">Display name from the MPass / local profile.</param>
/// <param name="Email">Email — null for accounts that did not opt in to email contact.</param>
/// <param name="State">
/// Stable string form of the underlying <c>UserAccountState</c> enum (e.g. <c>"Active"</c>,
/// <c>"Suspended"</c>, <c>"Disabled"</c>, <c>"Locked"</c>). Stringified at the boundary
/// so the WASM client can render the value without sharing the enum type.
/// </param>
/// <param name="Roles">Granted role codes (e.g. <c>cnas-user</c>, <c>cnas-admin</c>).</param>
[SensitivityClassification(SensitivityLabel.Internal,
    Reason = "Admin user-list rows ship internal-only operator data; Email is bumped to Confidential per-property.")]
public sealed record UserListItem(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    string DisplayName,
    [property: SensitivityClassification(SensitivityLabel.Confidential,
        Reason = "Email is citizen contact PII per R0228 / SEC 033 — matches ProfileOutput.Email.")]
    string? Email,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string State,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    IReadOnlyList<string> Roles);
