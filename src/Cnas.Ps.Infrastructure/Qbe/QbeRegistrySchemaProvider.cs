using System.Collections.Frozen;
using Cnas.Ps.Application.Qbe;
using Cnas.Ps.Application.QueryBudget;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Qbe;

/// <summary>
/// R0163 / TOR UI 009 — startup-seeded singleton schema provider. Holds the frozen
/// allow-list of queryable fields for every registry the QBE primitive supports.
/// </summary>
/// <remarks>
/// <para>
/// <b>Vertical-slice scope.</b> Solicitant (R0163), AuditLog (R0193), Declaration
/// (R0822), Document + Decision (R0671 continuation), Cerere (R0671 back-fill
/// continuation) schemas are wired in this build — WorkflowTask is deferred
/// until the QBE form lands in the Blazor UI. Adding a new schema is a one-line
/// change to <see cref="BuildSchemas"/>.
/// </para>
/// <para>
/// <b>Frozen dictionary.</b> <see cref="FrozenDictionary{TKey,TValue}"/> lookup
/// is the hot path on every QBE call; the frozen variant is O(1) and allocation-free.
/// </para>
/// </remarks>
public sealed class QbeRegistrySchemaProvider : IQbeRegistrySchemaProvider
{
    private readonly FrozenDictionary<string, QbeRegistrySchema> _schemas;

    /// <summary>Builds the provider, eagerly materialising the frozen schema set.</summary>
    public QbeRegistrySchemaProvider()
    {
        _schemas = BuildSchemas().ToFrozenDictionary(s => s.RegistryCode, StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public QbeRegistrySchema? GetForRegistry(string registryCode) =>
        string.IsNullOrEmpty(registryCode) ? null : _schemas.GetValueOrDefault(registryCode);

    /// <summary>
    /// Returns the seed list of registry schemas. Kept as a method (rather than a static
    /// readonly field) so the constructor can wire the dictionary without a circular-init
    /// dependency on the typeof-introspection in <see cref="QbeRegistrySchema"/>.
    /// </summary>
    /// <returns>The registry schemas wired in this build.</returns>
    private static IEnumerable<QbeRegistrySchema> BuildSchemas()
    {
        // Solicitant — applicant registry (TOR §2.3 #2). Field set mirrors the
        // entity's externally-queryable surface; encrypted columns (NationalId) and
        // bookkeeping (CreatedAtUtc) are filterable, but the encrypted plaintext
        // column itself is NOT — callers filter via NationalIdHash so the deterministic
        // hash is the only side-channel into the encrypted column.
        yield return new QbeRegistrySchema(
            RegistryCode: QueryBudgetRegistries.Solicitant,
            Fields: new[]
            {
                new QbeFieldSchema("Id", typeof(long)),
                new QbeFieldSchema("DisplayName", typeof(string)),
                new QbeFieldSchema("Email", typeof(string)),
                new QbeFieldSchema("PhoneE164", typeof(string)),
                // NationalIdHash carries deterministic base64 — case carries information
                // (uppercase vs lowercase change the underlying bytes), so the comparison
                // must be byte-for-byte equal.
                new QbeFieldSchema("NationalIdHash", typeof(string), IsCaseSensitive: true),
                new QbeFieldSchema("Kind", typeof(ApplicantKind)),
                new QbeFieldSchema("CreatedAtUtc", typeof(DateTime)),
                new QbeFieldSchema("IsActive", typeof(bool)),
                new QbeFieldSchema("PreferredLanguage", typeof(string)),
                new QbeFieldSchema("PostalAddress", typeof(string)),
                new QbeFieldSchema("BankIban", typeof(string)),
                new QbeFieldSchema("AffiliatedLegalEntityId", typeof(string)),
            });

        // R0193 / SEC 052 — AuditLog registry. Exposes only the externally-queryable
        // surface of the audit row; NationalIdHash and any other PII-bearing field
        // is intentionally NOT registered here (PII surface — defer to a future
        // permission-gated unmask flow). Field names match the
        // <see cref="Cnas.Ps.Core.Domain.AuditLog"/> property names so the
        // reflection-based LINQ converter can bind directly.
        yield return new QbeRegistrySchema(
            RegistryCode: QueryBudgetRegistries.AuditLog,
            Fields: new[]
            {
                new QbeFieldSchema("Id", typeof(long)),
                // EventAtUtc is the canonical business-event instant. CreatedAtUtc
                // is a synonym registered for spec parity — the underlying entity
                // property is also named CreatedAtUtc (on AuditableEntity) and
                // EF binds them as separate columns; both are safe to filter on.
                new QbeFieldSchema("EventAtUtc", typeof(DateTime)),
                new QbeFieldSchema("CreatedAtUtc", typeof(DateTime)),
                new QbeFieldSchema("EventCode", typeof(string)),
                new QbeFieldSchema("Severity", typeof(AuditSeverity)),
                new QbeFieldSchema("ActorId", typeof(string)),
                new QbeFieldSchema("TargetEntity", typeof(string)),
                new QbeFieldSchema("TargetEntityId", typeof(long)),
                new QbeFieldSchema("SourceIp", typeof(string)),
                new QbeFieldSchema("CorrelationId", typeof(string)),
            });

        // R0671 continuation — Document registry schema. Surfaces the externally-
        // queryable surface of the Document entity. The full citizen-supplied title
        // (which may carry PII fragments) is intentionally NOT registered as a
        // queryable field here — operators narrow by Kind / MimeType / size / date,
        // and the title surfaces in the projected list item but is not filterable
        // through this primitive. Field names match the entity property names so the
        // reflection-based LINQ converter binds directly.
        yield return new QbeRegistrySchema(
            RegistryCode: QueryBudgetRegistries.Document,
            Fields: new[]
            {
                new QbeFieldSchema("Id", typeof(long)),
                // DossierId is the polymorphic owner pointer; the projected DTO
                // surfaces it as OwnerEntitySqid. Nullable on unattached templates;
                // the converter handles null comparisons via IsNull / IsNotNull.
                new QbeFieldSchema("DossierId", typeof(long)),
                new QbeFieldSchema("Kind", typeof(DocumentKind)),
                new QbeFieldSchema("MimeType", typeof(string)),
                new QbeFieldSchema("SizeBytes", typeof(long)),
                new QbeFieldSchema("CreatedAtUtc", typeof(DateTime)),
                new QbeFieldSchema("IsSigned", typeof(bool)),
            });

        // R0671 continuation — Decision registry schema. The "decision" entity is the
        // Dossier row (TOR §2.3 #7); the decision lifecycle is tracked via the parent
        // ServiceApplication's Status field. Drafted = CreatedAtUtc; finalised =
        // ClosedAtUtc; drafted-by = AssignedExaminerId; approver = ApproverId.
        yield return new QbeRegistrySchema(
            RegistryCode: QueryBudgetRegistries.Decision,
            Fields: new[]
            {
                new QbeFieldSchema("Id", typeof(long)),
                new QbeFieldSchema("ApplicationId", typeof(long)),
                new QbeFieldSchema("DossierNumber", typeof(string)),
                new QbeFieldSchema("CreatedAtUtc", typeof(DateTime)),
                new QbeFieldSchema("ClosedAtUtc", typeof(DateTime)),
                new QbeFieldSchema("AssignedExaminerId", typeof(long)),
                new QbeFieldSchema("ApproverId", typeof(long)),
            });

        // R0822 / TOR Annex 8 BP 1.2-M — Declarations registry explorer schema.
        // Surfaces the canonical Annex 1 §8.1.3 search criteria: payer, kind,
        // reporting window, status, plus the R0821 / R0823 attribution columns
        // (RegisteredByOffice / FormVersion / HasScannedCopy). Money fields
        // (DeclaredContributionAmount) are filterable so an operator can pin
        // outlier amounts without leaving the registry.
        yield return new QbeRegistrySchema(
            RegistryCode: QueryBudgetRegistries.Declaration,
            Fields: new[]
            {
                new QbeFieldSchema("Id", typeof(long)),
                new QbeFieldSchema("ContributorId", typeof(long)),
                new QbeFieldSchema("Kind", typeof(DeclarationKind)),
                new QbeFieldSchema("ReportingMonth", typeof(DateOnly)),
                new QbeFieldSchema("FiledAtUtc", typeof(DateTime)),
                new QbeFieldSchema("Status", typeof(DeclarationStatus)),
                new QbeFieldSchema("ReferenceNumber", typeof(string)),
                new QbeFieldSchema("DeclaredContributionAmount", typeof(decimal)),
                new QbeFieldSchema("RegisteredByOffice", typeof(string)),
                new QbeFieldSchema("FormVersion", typeof(string)),
                new QbeFieldSchema("HasScannedCopy", typeof(bool)),
            });

        // R0671 back-fill continuation — Cerere (ServiceApplication) registry
        // schema. Surfaces the externally-queryable surface of the
        // <see cref="ServiceApplication"/> entity needed by the access-scope
        // back-fill helper: ids, status, time-stamps, and the natural-key
        // ReferenceNumber. The form-payload JSON column is intentionally NOT
        // registered — its shape varies per service and querying it through
        // the QBE primitive would leak the variable schema; targeted form
        // searches go through the bespoke application-search service instead.
        yield return new QbeRegistrySchema(
            RegistryCode: QueryBudgetRegistries.Cerere,
            Fields: new[]
            {
                new QbeFieldSchema("Id", typeof(long)),
                new QbeFieldSchema("SolicitantId", typeof(long)),
                new QbeFieldSchema("ServicePassportId", typeof(long)),
                new QbeFieldSchema("Status", typeof(ApplicationStatus)),
                new QbeFieldSchema("CreatedAtUtc", typeof(DateTime)),
                new QbeFieldSchema("SubmittedAtUtc", typeof(DateTime)),
                new QbeFieldSchema("ClosedAtUtc", typeof(DateTime)),
                new QbeFieldSchema("ReferenceNumber", typeof(string)),
            });

        // R1600 / TOR Annex 3.8 — executory-documents registry schema.
        // Surfaces the queryable surface needed by the registry browser:
        // series number, kind, status, priority, dates, mode and creditor.
        // Plaintext IDNP / IBAN are NEVER registered — they are PII /
        // financially-sensitive and the registry exposes lookup-by-debtor
        // through a dedicated endpoint that hashes the IDNP for equality.
        yield return new QbeRegistrySchema(
            RegistryCode: QueryBudgetRegistries.ExecutoryDocument,
            Fields: new[]
            {
                new QbeFieldSchema("Id", typeof(long)),
                new QbeFieldSchema("DocumentSeriesNumber", typeof(string)),
                new QbeFieldSchema("Kind", typeof(ExecutoryDocumentKind)),
                new QbeFieldSchema("Status", typeof(ExecutoryDocumentStatus)),
                new QbeFieldSchema("PriorityRank", typeof(int)),
                new QbeFieldSchema("IssuedDate", typeof(DateOnly)),
                new QbeFieldSchema("EffectiveFrom", typeof(DateOnly)),
                new QbeFieldSchema("EffectiveUntil", typeof(DateOnly)),
                new QbeFieldSchema("WithholdingMode", typeof(ExecutoryDocumentWithholdingMode)),
                new QbeFieldSchema("CreditorName", typeof(string)),
            });

        // R2270 / TOR SEC 023-024 — user-group registry schema. Volume is
        // low (a few hundred groups per organisation) but admin tooling
        // queries the registry through QBE alongside everything else.
        yield return new QbeRegistrySchema(
            RegistryCode: QueryBudgetRegistries.UserGroup,
            Fields: new[]
            {
                new QbeFieldSchema("Code", typeof(string)),
                new QbeFieldSchema("DisplayName", typeof(string)),
                new QbeFieldSchema("Kind", typeof(UserGroupKind)),
                new QbeFieldSchema("Status", typeof(UserGroupStatus)),
            });
    }
}
