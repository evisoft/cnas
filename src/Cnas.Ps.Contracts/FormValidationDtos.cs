namespace Cnas.Ps.Contracts;

/// <summary>
/// Input DTO carrying a candidate form payload to be validated server-side against the
/// schema declared on the referenced <c>ServicePassport</c>. UC07 — "Înregistrare
/// formular". The validation is performed BEFORE the workflow starts so that obviously
/// broken submissions never reach the decision engine.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ServicePassportId"/> is a Sqid-encoded identifier (CLAUDE.md RULE 3); the
/// service layer decodes it to the underlying database key. <see cref="FormPayloadJson"/>
/// is a free-form JSON object whose shape is constrained by
/// <c>ServicePassport.FormSchemaJson</c>.
/// </para>
/// <para>
/// On success the endpoint returns 200 with an empty body. On a validation failure
/// (missing required fields, type mismatches, pattern/range violations) the response is
/// 400 with a <c>ProblemDetails</c> body whose <c>detail</c> is a semicolon-joined list
/// of the violated rules.
/// </para>
/// </remarks>
/// <param name="ServicePassportId">Sqid-encoded id of the service passport whose schema drives validation.</param>
/// <param name="FormPayloadJson">JSON object string carrying the candidate form values.</param>
public sealed record FormValidationRequest(
    string ServicePassportId,
    string FormPayloadJson);
