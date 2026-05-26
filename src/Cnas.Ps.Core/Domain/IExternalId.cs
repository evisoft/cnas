namespace Cnas.Ps.Core.Domain;

/// <summary>
/// Marker contract identifying domain entities whose BIGINT primary key crosses the
/// system boundary into output DTOs and therefore MUST be Sqid-encoded per CLAUDE.md
/// RULE 3 / ARH 027.
/// </summary>
/// <remarks>
/// <para>
/// The interface has no members — its presence is the contract. The companion
/// architecture test
/// <c>ExternalIdContractTests.DtosWithStringId_MapToEntitiesImplementingIExternalId</c>
/// asserts that every Contracts DTO whose <c>Id</c> field is a string has a
/// corresponding domain entity that implements this interface.
/// </para>
/// <para>
/// Internal-only entities (e.g. join tables, audit shadows, BPMN definitions looked up
/// by stable string <c>Code</c>) MUST NOT implement <see cref="IExternalId"/>; doing so
/// would imply their surrogate ids are also part of the public contract, which is not
/// the case.
/// </para>
/// <para>
/// The marker is purely a documentation + architectural-test contract. It has no
/// runtime behaviour and does not influence EF Core mapping, serialization, or
/// DI registration.
/// </para>
/// </remarks>
public interface IExternalId
{
}
