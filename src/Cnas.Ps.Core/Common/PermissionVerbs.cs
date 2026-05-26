namespace Cnas.Ps.Core.Common;

/// <summary>
/// R0673 / TOR CF 18.12 — stable verbs enumerating the granular permission matrix
/// that complements the coarse <see cref="RoleCodes"/>. Each verb describes one
/// action that may be granted to a role against a specific resource type
/// (e.g. <c>Resource="Dossier"</c>, <c>Verb=Modify</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Stability contract.</b> The string values are part of the public contract —
/// they are persisted in <see cref="Cnas.Ps.Core.Domain.GranularPermissionAssignment"/>
/// rows and returned to the admin REST surface. Renaming a verb is a breaking
/// change. Add new verbs by appending; never reuse a retired value.
/// </para>
/// <para>
/// <b>Naming convention.</b> PascalCase nouns / noun-phrases. Today's verbs mirror
/// the CF 18.12 enumeration: <c>View</c>, <c>Add</c>, <c>Modify</c>,
/// <c>StatusChange</c>, <c>Generate</c>, <c>Download</c>.
/// </para>
/// <para>
/// <b>Resource discriminator.</b> The verb is meaningless without a resource type
/// — the assignment row pairs a verb with a stable resource name (e.g.
/// <c>"Dossier"</c>, <c>"Document"</c>, <c>"Solicitant"</c>). The resource string
/// is a free-form PascalCase identifier; the architecture tests do not gate it
/// against a fixed allow-list so new resources can be added without a Core change.
/// </para>
/// </remarks>
public static class PermissionVerbs
{
    /// <summary>Read access — open a record for inspection but not modify it.</summary>
    public const string View = "View";

    /// <summary>Create a new instance of the resource (e.g. open a new dossier).</summary>
    public const string Add = "Add";

    /// <summary>Modify an existing record's mutable fields.</summary>
    public const string Modify = "Modify";

    /// <summary>
    /// Transition the record between workflow states (e.g. Draft → Submitted).
    /// Distinct from <see cref="Modify"/> because state-machine transitions carry
    /// stricter audit and approval requirements than ordinary field edits.
    /// </summary>
    public const string StatusChange = "StatusChange";

    /// <summary>
    /// Produce a derived artefact from the record (e.g. render a decision PDF).
    /// Distinct from <see cref="Add"/> because the action does not persist a new
    /// row of the same resource — it emits a downstream artefact instead.
    /// </summary>
    public const string Generate = "Generate";

    /// <summary>
    /// Retrieve a binary copy of the record or one of its artefacts. Distinct from
    /// <see cref="View"/> because the act of downloading carries an evidentiary
    /// weight (the bytes leave the system) that warrants a separate gate.
    /// </summary>
    public const string Download = "Download";

    /// <summary>
    /// All known verbs in declaration order. Used by validators that want to
    /// gate a free-text verb against the known set without hand-maintaining a
    /// parallel list.
    /// </summary>
    public static readonly System.Collections.Generic.IReadOnlySet<string> All =
        new System.Collections.Generic.HashSet<string>(
            new[] { View, Add, Modify, StatusChange, Generate, Download },
            System.StringComparer.Ordinal);
}
