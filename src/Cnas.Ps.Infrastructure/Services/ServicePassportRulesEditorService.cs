using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// R0141 / TOR CF 15.03 — concrete <see cref="IServicePassportRulesEditorService"/>.
/// Persists business rules as a <c>businessRules</c> JSON array inside the
/// passport's existing <c>DecisionRulesJson</c> column so the runtime
/// <c>IDecisionEngine</c> can co-exist with the editor without a schema cascade.
/// </summary>
/// <remarks>
/// <para>
/// <b>Storage layout.</b> Each business rule is a JSON object inside the
/// top-level <c>businessRules</c> array:
/// <code>
/// {
///   "code": "BIRTH_GRANT",
///   "eligibility": [ ... ],
///   "amount": { ... },
///   "businessRules": [
///     { "id": "abc123...", "name": "...", "applicantType": "Natural",
///       "condition": { ... }, "decisionOutcome": "Granted", "notes": null }
///   ]
/// }
/// </code>
/// The eligibility / amount sections are left intact so existing engine flows
/// remain unchanged. The editor only touches <c>businessRules</c>.
/// </para>
/// <para>
/// <b>Rule id derivation.</b> The opaque id is a deterministic Base32-encoded
/// SHA-256 prefix (16 chars = 80 bits, collision-safe enough at the per-passport
/// scale) computed over the rule's stable identity (name + applicant type +
/// condition JSON). This means the same logical rule keeps the same id across
/// save round-trips and across passport version bumps — admin URLs stay stable.
/// </para>
/// </remarks>
public sealed class ServicePassportRulesEditorService(
    ICnasDbContext db,
    ICnasTimeProvider clock,
    ICallerContext caller,
    IAuditService audit,
    IValidator<BusinessRuleInputDto> inputValidator,
    IDecisionEngine engine) : IServicePassportRulesEditorService
{
    private readonly ICnasDbContext _db = db;
    private readonly ICnasTimeProvider _clock = clock;
    private readonly ICallerContext _caller = caller;
    private readonly IAuditService _audit = audit;
    private readonly IValidator<BusinessRuleInputDto> _validator = inputValidator;
    private readonly IDecisionEngine _engine = engine;

    /// <summary>Stable audit-event code for business-rule mutations.</summary>
    private const string AuditEvent = "SERVICEPASSPORT.BUSINESSRULE_CHANGED";

    /// <summary>Top-level JSON property carrying the business-rule array.</summary>
    internal const string BusinessRulesProperty = "businessRules";

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<BusinessRuleDto>>> ListRulesAsync(
        string passportCode,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(passportCode))
        {
            return Result<IReadOnlyList<BusinessRuleDto>>.Failure(
                ErrorCodes.NotFound, "Passport code is required.");
        }

        var passport = await LoadCurrentPassportAsync(passportCode, cancellationToken).ConfigureAwait(false);
        if (passport is null)
        {
            return Result<IReadOnlyList<BusinessRuleDto>>.Failure(
                ErrorCodes.NotFound, $"Service passport '{passportCode}' not found.");
        }

        var rules = ParseRules(passport.DecisionRulesJson);
        return Result<IReadOnlyList<BusinessRuleDto>>.Success(rules);
    }

    /// <inheritdoc />
    public async Task<Result<BusinessRuleDto>> UpsertRuleAsync(
        string passportCode,
        BusinessRuleInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Step 1 — structural validation.
        var validation = await _validator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<BusinessRuleDto>.Failure(
                ErrorCodes.ValidationFailed,
                string.Join("; ", validation.Errors.Select(e => e.ErrorMessage)));
        }

        // Step 2 — passport lookup.
        var passport = await LoadCurrentPassportAsync(passportCode, cancellationToken).ConfigureAwait(false);
        if (passport is null)
        {
            return Result<BusinessRuleDto>.Failure(
                ErrorCodes.NotFound, $"Service passport '{passportCode}' not found.");
        }

        var rules = ParseRules(passport.DecisionRulesJson).ToList();
        var derivedId = ComputeRuleId(input.Name, input.ApplicantType, input.ConditionJson);

        // Resolve target index — update branch if id supplied AND found.
        var existingIndex = -1;
        if (!string.IsNullOrWhiteSpace(input.Id))
        {
            existingIndex = rules.FindIndex(r =>
                string.Equals(r.Id, input.Id, StringComparison.Ordinal));
            if (existingIndex < 0)
            {
                return Result<BusinessRuleDto>.Failure(
                    ErrorCodes.NotFound, $"Business rule '{input.Id}' not found on passport '{passportCode}'.");
            }
        }
        else
        {
            // Create branch — defensive de-dup: if a rule already exists with
            // the same derived id (same name + type + condition) we overwrite
            // it in place rather than creating a duplicate.
            existingIndex = rules.FindIndex(r =>
                string.Equals(r.Id, derivedId, StringComparison.Ordinal));
        }

        var newRule = new BusinessRuleDto(
            Id: derivedId,
            Name: input.Name,
            ApplicantType: input.ApplicantType,
            ConditionJson: input.ConditionJson,
            DecisionOutcome: input.DecisionOutcome,
            Notes: input.Notes);

        if (existingIndex >= 0)
        {
            rules[existingIndex] = newRule;
        }
        else
        {
            rules.Add(newRule);
        }

        // Step 3 — serialise the merged document and re-validate via the engine
        // parser. A parser failure surfaces as ValidationFailed so the admin UI
        // can show the exact reason.
        var originalRulesJson = passport.DecisionRulesJson;
        var serialised = SerializeRules(originalRulesJson, rules);
        var parseCheck = ValidateEngineParseable(originalRulesJson, serialised);
        if (parseCheck.IsFailure)
        {
            return Result<BusinessRuleDto>.Failure(parseCheck.ErrorCode!, parseCheck.ErrorMessage!);
        }

        passport.DecisionRulesJson = serialised;
        passport.UpdatedAtUtc = _clock.UtcNow;
        passport.UpdatedBy = _caller.UserSqid;

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await _audit.RecordAsync(
            eventCode: AuditEvent,
            severity: AuditSeverity.Notice,
            actorId: _caller.UserSqid ?? "system",
            targetEntity: nameof(ServicePassport),
            targetEntityId: passport.Id,
            detailsJson: JsonSerializer.Serialize(new
            {
                code = passport.Code,
                ruleId = derivedId,
                action = existingIndex >= 0 ? "updated" : "created",
                applicantType = newRule.ApplicantType.ToString(),
                outcome = newRule.DecisionOutcome.ToString(),
            }),
            sourceIp: _caller.SourceIp,
            correlationId: _caller.CorrelationId,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return Result<BusinessRuleDto>.Success(newRule);
    }

    /// <inheritdoc />
    public async Task<Result> DeleteRuleAsync(
        string passportCode,
        string ruleSqid,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ruleSqid))
        {
            return Result.Failure(ErrorCodes.NotFound, "Rule id is required.");
        }

        var passport = await LoadCurrentPassportAsync(passportCode, cancellationToken).ConfigureAwait(false);
        if (passport is null)
        {
            return Result.Failure(ErrorCodes.NotFound, $"Service passport '{passportCode}' not found.");
        }

        var rules = ParseRules(passport.DecisionRulesJson).ToList();
        var removedIndex = rules.FindIndex(r => string.Equals(r.Id, ruleSqid, StringComparison.Ordinal));
        if (removedIndex < 0)
        {
            return Result.Failure(ErrorCodes.NotFound,
                $"Business rule '{ruleSqid}' not found on passport '{passportCode}'.");
        }

        rules.RemoveAt(removedIndex);
        passport.DecisionRulesJson = SerializeRules(passport.DecisionRulesJson, rules);
        passport.UpdatedAtUtc = _clock.UtcNow;
        passport.UpdatedBy = _caller.UserSqid;

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await _audit.RecordAsync(
            eventCode: AuditEvent,
            severity: AuditSeverity.Notice,
            actorId: _caller.UserSqid ?? "system",
            targetEntity: nameof(ServicePassport),
            targetEntityId: passport.Id,
            detailsJson: JsonSerializer.Serialize(new
            {
                code = passport.Code,
                ruleId = ruleSqid,
                action = "deleted",
            }),
            sourceIp: _caller.SourceIp,
            correlationId: _caller.CorrelationId,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return Result.Success();
    }

    /// <summary>
    /// Looks up the current (<c>IsCurrent=true</c>, <c>IsActive=true</c>) row for
    /// <paramref name="passportCode"/>. Returns null when no such row exists.
    /// </summary>
    /// <param name="passportCode">Stable logical passport code.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The tracked current passport row, or null when missing.</returns>
    private Task<ServicePassport?> LoadCurrentPassportAsync(
        string passportCode,
        CancellationToken cancellationToken)
        => _db.ServicePassports
            .SingleOrDefaultAsync(
                p => p.Code == passportCode && p.IsCurrent && p.IsActive,
                cancellationToken);

    /// <summary>
    /// Parses the <c>businessRules</c> array out of <paramref name="decisionRulesJson"/>.
    /// Returns an empty list when the column is null/empty, the JSON is malformed,
    /// or the property is missing. Malformed input never throws — the editor
    /// degrades to "no rules yet" so a corrupted column does not lock operators
    /// out of the editor; the upsert path then re-serialises into a clean shape.
    /// </summary>
    /// <param name="decisionRulesJson">Raw column value.</param>
    /// <returns>Parsed business rules in document order.</returns>
    internal static IReadOnlyList<BusinessRuleDto> ParseRules(string? decisionRulesJson)
    {
        if (string.IsNullOrWhiteSpace(decisionRulesJson))
        {
            return Array.Empty<BusinessRuleDto>();
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(decisionRulesJson);
        }
        catch (JsonException)
        {
            return Array.Empty<BusinessRuleDto>();
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Array.Empty<BusinessRuleDto>();
            }

            if (!document.RootElement.TryGetProperty(BusinessRulesProperty, out var rulesProp)
                || rulesProp.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<BusinessRuleDto>();
            }

            var result = new List<BusinessRuleDto>(rulesProp.GetArrayLength());
            foreach (var node in rulesProp.EnumerateArray())
            {
                if (node.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var id = node.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String
                    ? idProp.GetString()
                    : null;
                var name = node.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String
                    ? nameProp.GetString()
                    : null;
                var applicantTypeStr = node.TryGetProperty("applicantType", out var atProp)
                    && atProp.ValueKind == JsonValueKind.String ? atProp.GetString() : null;
                var outcomeStr = node.TryGetProperty("decisionOutcome", out var doProp)
                    && doProp.ValueKind == JsonValueKind.String ? doProp.GetString() : null;
                var notes = node.TryGetProperty("notes", out var notesProp)
                    && notesProp.ValueKind == JsonValueKind.String ? notesProp.GetString() : null;

                string conditionJson;
                if (node.TryGetProperty("condition", out var condProp))
                {
                    conditionJson = condProp.GetRawText();
                }
                else
                {
                    conditionJson = "{}";
                }

                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                if (!Enum.TryParse<BusinessRuleApplicantType>(applicantTypeStr, ignoreCase: false, out var applicantType))
                {
                    applicantType = BusinessRuleApplicantType.Both;
                }
                if (!Enum.TryParse<BusinessRuleDecisionOutcome>(outcomeStr, ignoreCase: false, out var outcome))
                {
                    outcome = BusinessRuleDecisionOutcome.RequiresReview;
                }

                result.Add(new BusinessRuleDto(
                    Id: id!,
                    Name: name!,
                    ApplicantType: applicantType,
                    ConditionJson: conditionJson,
                    DecisionOutcome: outcome,
                    Notes: notes));
            }
            return result;
        }
    }

    /// <summary>
    /// Re-serialises the rule list into the existing <c>DecisionRulesJson</c>
    /// document, preserving every other top-level property (e.g. <c>eligibility</c>,
    /// <c>amount</c>, <c>code</c>, <c>successCode</c>) so the runtime engine sees
    /// no change to its inputs.
    /// </summary>
    /// <param name="originalDecisionRulesJson">The unmodified column value.</param>
    /// <param name="rules">The desired business-rule list (replaces the array).</param>
    /// <returns>The merged JSON document text.</returns>
    internal static string SerializeRules(string? originalDecisionRulesJson, IReadOnlyList<BusinessRuleDto> rules)
    {
        // Build a fresh object whose properties are: (a) every original
        // property except businessRules, then (b) the rebuilt businessRules
        // array. We walk the original document to preserve property order.
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();

            if (!string.IsNullOrWhiteSpace(originalDecisionRulesJson))
            {
                JsonDocument? originalDoc = null;
                try
                {
                    originalDoc = JsonDocument.Parse(originalDecisionRulesJson);
                }
                catch (JsonException)
                {
                    // Corrupted column — degrade silently to "fresh shape", the
                    // upsert / delete is itself recovering the document.
                }

                if (originalDoc is not null)
                {
                    using (originalDoc)
                    {
                        if (originalDoc.RootElement.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var prop in originalDoc.RootElement.EnumerateObject())
                            {
                                if (string.Equals(prop.Name, BusinessRulesProperty, StringComparison.Ordinal))
                                {
                                    continue;
                                }
                                prop.WriteTo(writer);
                            }
                        }
                    }
                }
            }

            writer.WritePropertyName(BusinessRulesProperty);
            writer.WriteStartArray();
            foreach (var rule in rules)
            {
                writer.WriteStartObject();
                writer.WriteString("id", rule.Id);
                writer.WriteString("name", rule.Name);
                writer.WriteString("applicantType", rule.ApplicantType.ToString());
                writer.WritePropertyName("condition");
                // The condition is itself JSON — emit the raw bytes so we
                // don't double-escape the payload.
                using (var condDoc = JsonDocument.Parse(rule.ConditionJson))
                {
                    condDoc.RootElement.WriteTo(writer);
                }
                writer.WriteString("decisionOutcome", rule.DecisionOutcome.ToString());
                if (rule.Notes is null)
                {
                    writer.WriteNull("notes");
                }
                else
                {
                    writer.WriteString("notes", rule.Notes);
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }

    /// <summary>
    /// Validates the merged JSON parses through the same
    /// <see cref="JsonDocument.Parse(string, JsonDocumentOptions)"/> path used
    /// by <see cref="JsonRulesDecisionEngine"/>. We do not run the full engine
    /// <c>Evaluate</c> because the original passport's eligibility / amount
    /// sections may already be empty (e.g. fresh seeds) — running the engine
    /// would surface a <c>BAD_RULE</c> on the pre-existing payload and block
    /// the editor from saving its own additive change. Instead we differentially
    /// validate: if the original JSON was already engine-parseable, the merged
    /// JSON MUST still be — otherwise we surface ValidationFailed with the
    /// engine's exact error. If the original was already malformed we let the
    /// editor recover the document.
    /// </summary>
    /// <param name="originalDecisionRulesJson">The unmodified passport column value.</param>
    /// <param name="decisionRulesJson">The merged DecisionRulesJson text.</param>
    /// <returns>Success when the document is well-formed; ValidationFailed on a regression.</returns>
    private Result ValidateEngineParseable(string? originalDecisionRulesJson, string decisionRulesJson)
    {
        try
        {
            using var _ = JsonDocument.Parse(decisionRulesJson);
        }
        catch (JsonException ex)
        {
            return Result.Failure(ErrorCodes.ValidationFailed,
                $"Merged DecisionRulesJson is not well-formed: {ex.Message}");
        }

        // Differential engine probe — only assert no regression if the
        // ORIGINAL was already engine-runnable. Legacy seeds may carry a
        // metadata-only payload (e.g. {"code":"TEST"}) that the engine
        // reports as BadRule because no amount block is present; the editor
        // must not penalise the operator for a pre-existing limitation it
        // didn't cause.
        if (!string.IsNullOrWhiteSpace(originalDecisionRulesJson))
        {
            var originalProbe = _engine.Evaluate(
                originalDecisionRulesJson,
                new DecisionFacts(new Dictionary<string, object?>()));
            var originalWasParseable = !(originalProbe.IsFailure
                && string.Equals(originalProbe.ErrorCode, ErrorCodes.BadRule, StringComparison.Ordinal));
            if (originalWasParseable)
            {
                var probe = _engine.Evaluate(
                    decisionRulesJson,
                    new DecisionFacts(new Dictionary<string, object?>()));
                if (probe.IsFailure
                    && string.Equals(probe.ErrorCode, ErrorCodes.BadRule, StringComparison.Ordinal))
                {
                    return Result.Failure(ErrorCodes.ValidationFailed, probe.ErrorMessage ?? "Bad rule");
                }
            }
        }
        return Result.Success();
    }

    /// <summary>
    /// Computes a deterministic opaque id for a business rule based on its
    /// stable identity (name + applicant type + condition JSON shape). The id
    /// is a 16-character Base32 (Crockford alphabet) prefix of the SHA-256
    /// digest — 80 bits, collision-safe at the per-passport scale.
    /// </summary>
    /// <param name="name">Operator-visible rule name.</param>
    /// <param name="applicantType">Applicant-type filter.</param>
    /// <param name="conditionJson">Condition JSON text.</param>
    /// <returns>The 16-char Base32 id.</returns>
    internal static string ComputeRuleId(
        string name,
        BusinessRuleApplicantType applicantType,
        string conditionJson)
    {
        var payload = $"{name}{applicantType}{conditionJson}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return EncodeBase32(bytes, charCount: 16);
    }

    /// <summary>
    /// Encodes the leading bytes of <paramref name="data"/> into a Crockford-
    /// style Base32 string of exactly <paramref name="charCount"/> characters.
    /// Uses the canonical Crockford alphabet (no I / O / U / L for human
    /// distinguishability) — the result is URL-safe.
    /// </summary>
    /// <param name="data">SHA-256 digest bytes.</param>
    /// <param name="charCount">Number of characters to emit.</param>
    /// <returns>Base32-encoded id of the requested length.</returns>
    private static string EncodeBase32(ReadOnlySpan<byte> data, int charCount)
    {
        const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"; // Crockford
        var output = new char[charCount];
        var bits = 0;
        var value = 0;
        var emitted = 0;
        for (var i = 0; i < data.Length && emitted < charCount; i++)
        {
            value = (value << 8) | data[i];
            bits += 8;
            while (bits >= 5 && emitted < charCount)
            {
                bits -= 5;
                output[emitted++] = Alphabet[(value >> bits) & 0x1F];
            }
        }
        return new string(output);
    }
}
