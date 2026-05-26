namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R0016 — marker contract identifying domain entities whose business identity is
/// expressed by a stable, human-readable <c>Code</c> column IN ADDITION to (or
/// instead of) the Sqid-encoded surrogate id. Documents intent: the entity's
/// <c>Code</c> is part of the public contract and MUST stay stable across versions —
/// changing it is a breaking change to every external reference that holds the value.
/// </summary>
/// <remarks>
/// <para>
/// <b>How it differs from <see cref="IExternalId"/>.</b> <see cref="IExternalId"/>
/// asserts that the entity's BIGINT primary key is exposed (Sqid-encoded) at the
/// system boundary. <see cref="IExternalCodeOwner"/> asserts that the entity ALSO
/// has a stable string code that itself crosses the boundary and is referenced by
/// administrators, partner systems, and other entities (e.g.
/// <see cref="ServicePassport.WorkflowCode"/> points at
/// <see cref="WorkflowDefinition.Code"/>). The two interfaces are orthogonal — an
/// entity may implement neither, either, or both.
/// </para>
/// <para>
/// <b>When to add.</b> Per the R0016 TODO note, the marker is added only where the
/// TOR / CF actually demands a stable external code beyond a Sqid. Tagging
/// arbitrary entities pollutes the contract surface. Today the deliberate carriers
/// are <see cref="ServicePassport"/> (e.g. <c>SP-001-BIRTH</c>) and
/// <see cref="WorkflowDefinition"/> (e.g. <c>WF-PENSION-AGE</c>); future use-cases
/// may extend the list as new "code-keyed" entities surface.
/// </para>
/// <para>
/// <b>No runtime behaviour.</b> Like <see cref="IExternalId"/>, this marker has no
/// members and influences no DI / serialisation / EF mapping. Its sole purpose is
/// documentation + future architecture-test enforcement (e.g. asserting that every
/// implementer exposes a non-null <c>Code</c> with the canonical kebab/SCREAMING
/// shape).
/// </para>
/// </remarks>
public interface IExternalCodeOwner
{
}
