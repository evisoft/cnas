namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R0122 / TOR CF 16.07 — describes the kind of performer that a workflow step
/// routes work to. The strongly-typed enum replaces ad-hoc JSON strings on workflow
/// step definitions and lets validators reason about the shape of the
/// <see cref="Cnas.Ps.Core.ValueObjects.WorkflowPerformerAssignment.Code"/> field
/// without parsing magic keywords.
/// </summary>
/// <remarks>
/// <para>
/// <b>Enum-value stability.</b> Numeric values are part of the persistence + wire
/// contract (the enum's <c>ToString()</c> name is serialised into workflow
/// definition JSON columns and DTOs). Renumbering or renaming an existing value is
/// a breaking change — append new kinds at the end of the list.
/// </para>
/// </remarks>
public enum WorkflowPerformerKind
{
    /// <summary>The step is routed to any holder of the named role (matched against
    /// <see cref="Cnas.Ps.Core.Common.RoleCodes"/>).</summary>
    Role = 0,

    /// <summary>The step is routed to any member of the named group
    /// (<see cref="UserGroup"/>).</summary>
    Group = 1,

    /// <summary>The step is routed to a specific user identified by Sqid.</summary>
    NamedUser = 2,

    /// <summary>The step is routed back to the originator of the workflow case
    /// (typically the citizen or front-desk operator who opened the application).
    /// Carries no <c>Code</c> — the assignment is reflexive.</summary>
    Originator = 3,

    /// <summary>The step is routed to the supervisor of the case originator. Carries
    /// no <c>Code</c> — the supervisor is resolved via the org-hierarchy lookup at
    /// dispatch time.</summary>
    Supervisor = 4,
}
