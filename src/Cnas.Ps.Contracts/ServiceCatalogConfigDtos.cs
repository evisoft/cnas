using System.Collections.Generic;

namespace Cnas.Ps.Contracts;

/// <summary>
/// R2163 / INT 004 — schema-driven new-service provisioning input. Supplied by an
/// administrator to <c>POST /api/admin/service-catalog/provision</c> to register a new
/// electronic service (a <c>ServicePassport</c> row + default workflow placeholder) WITHOUT
/// touching code or migrations.
/// </summary>
/// <remarks>
/// <para>
/// The schema-driven contract reflects TOR §15.4 INT 004: a new web-service can be added
/// to the SI-PS catalogue via configuration alone. The validator
/// <c>NewServiceProvisionInputValidator</c> enforces shape (stable code regex, schema with
/// at least one declared field, known workflow code).
/// </para>
/// <para>
/// The Sqid contract from CLAUDE.md RULE 3 still applies to the <em>output</em> DTO
/// <see cref="NewServiceProvisionDto"/>: the persisted passport row id is Sqid-encoded
/// even though the inbound payload carries no id (provisioning is by-code, not by-id).
/// </para>
/// </remarks>
/// <param name="Code">Stable upper-snake/dash code (3-32 chars, e.g. <c>SP-NEW-INT004</c>). Becomes <c>ServicePassport.Code</c>.</param>
/// <param name="NameRo">Romanian display name (3-256 chars).</param>
/// <param name="NameEn">Optional English display name.</param>
/// <param name="NameRu">Optional Russian display name.</param>
/// <param name="DescriptionRo">Multi-line Romanian description (non-empty).</param>
/// <param name="WorkflowCode">Stable workflow code that runs when the service is invoked (must reference a known workflow).</param>
/// <param name="MaxProcessingDays">SLA window in days (1-365).</param>
/// <param name="FormSchemaJson">JSON-schema declaring the form fields. Must include at least one property under <c>properties</c>.</param>
/// <param name="DecisionRulesJson">Declarative rule-set (JSON). Supply <c>"{}"</c> for an empty rule-set.</param>
/// <param name="ClassifierSchemes">Classifier scheme codes the new service references (used to register lookups). Empty for services without classifier dependencies.</param>
/// <param name="IsEnabled">True when the service should be exposed to new submissions immediately.</param>
/// <param name="IsProactive">True when the service can be initiated proactively by an event (UC14).</param>
public sealed record NewServiceProvisionInputDto(
    string Code,
    string NameRo,
    string? NameEn,
    string? NameRu,
    string DescriptionRo,
    string WorkflowCode,
    int MaxProcessingDays,
    string FormSchemaJson,
    string DecisionRulesJson,
    IReadOnlyList<string> ClassifierSchemes,
    bool IsEnabled,
    bool IsProactive);

/// <summary>
/// R2163 / INT 004 — output of a successful schema-driven provisioning call. Carries
/// the Sqid-encoded id of the newly-created <c>ServicePassport</c> row plus the canonical
/// code so the admin UI can deep-link to the catalogue page.
/// </summary>
/// <param name="Id">Sqid-encoded passport row id (CLAUDE.md RULE 3).</param>
/// <param name="Code">Canonical passport code (upper-case, trimmed).</param>
/// <param name="WorkflowCode">Canonical workflow code persisted on the passport.</param>
/// <param name="Version">Initial version (always 1 for a freshly-provisioned passport).</param>
public sealed record NewServiceProvisionDto(
    string Id,
    string Code,
    string WorkflowCode,
    int Version);

/// <summary>
/// R2163 / INT 004 — retirement input for an existing service-catalog entry.
/// Soft-deactivates the current <c>ServicePassport</c> row (flips <c>IsEnabled=false</c>
/// without breaking the version chain) and emits a <c>SERVICE.RETIRED</c> critical audit.
/// </summary>
/// <param name="Reason">Non-empty operator reason (audited verbatim).</param>
public sealed record ServiceRetirementInputDto(
    string Reason);
