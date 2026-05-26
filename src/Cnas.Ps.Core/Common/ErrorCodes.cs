namespace Cnas.Ps.Core.Common;

/// <summary>
/// Stable error codes returned by service layer operations.
/// Codes are SCREAMING_SNAKE_CASE and grouped by domain. Once published, codes are
/// part of the public contract — renaming is a breaking change.
/// See CLAUDE.md §2.2.
/// </summary>
public static class ErrorCodes
{
    // --- Generic ---

    /// <summary>The requested resource could not be located.</summary>
    public const string NotFound = "NOT_FOUND";

    /// <summary>The input failed validation. Combine with field-level details for the caller.</summary>
    public const string ValidationFailed = "VALIDATION_FAILED";

    /// <summary>The caller is not authenticated.</summary>
    public const string Unauthorized = "UNAUTHORIZED";

    /// <summary>The caller is authenticated but lacks permission for this resource/action.</summary>
    public const string Forbidden = "FORBIDDEN";

    /// <summary>Operation conflicts with the current state of the resource (e.g., concurrency).</summary>
    public const string Conflict = "CONFLICT";

    /// <summary>
    /// Optimistic-concurrency conflict — the row was modified by a concurrent transaction
    /// between read and save. Distinct from <see cref="Conflict"/> so callers can branch on
    /// "retry the read-modify-write" specifically (e.g. workflow-definition publishes —
    /// see <c>WorkflowConfigurationService.SaveDefinitionAsync</c>). Maps to HTTP 409.
    /// </summary>
    public const string ConcurrencyConflict = "CONCURRENCY_CONFLICT";

    /// <summary>An unexpected internal error occurred; the request was correlated via X-Correlation-Id.</summary>
    public const string Internal = "INTERNAL_ERROR";

    /// <summary>Rate limit exceeded on the public surface.</summary>
    public const string RateLimited = "RATE_LIMITED";

    /// <summary>
    /// The requested feature is recognised but not implemented in this build (deferred
    /// by design — e.g. R0305 / BP 1.6 Contributor split). Mapped to HTTP 501 by the
    /// controller. Distinct from <see cref="Internal"/> so operations dashboards do
    /// not page on intentional placeholders.
    /// </summary>
    public const string NotImplemented = "NOT_IMPLEMENTED";

    // --- Sqids / IDs ---

    /// <summary>The supplied external identifier is not a valid Sqid.</summary>
    public const string InvalidSqid = "INVALID_SQID";

    // --- Applications (Cerere) ---

    /// <summary>The application is missing one or more required attached documents (per TOR §2.5.1).</summary>
    public const string ApplicationIncomplete = "APPLICATION_INCOMPLETE";

    /// <summary>The application has already been processed and cannot be modified.</summary>
    public const string ApplicationLocked = "APPLICATION_LOCKED";

    /// <summary>
    /// The application is not in the <c>Submitted</c> state and therefore cannot be
    /// advanced through the intake → examination transition. Returned from
    /// <c>IApplicationProcessingService.AdvanceAsync</c> as a Conflict.
    /// </summary>
    public const string ApplicationNotSubmitted = "APPLICATION_NOT_SUBMITTED";

    /// <summary>
    /// R0321 / R0224 / UI 008 — the target <see cref="Cnas.Ps.Core.Domain.ServiceApplication"/>
    /// is in a terminal status (Closed / Approved / Rejected / Withdrawn) and therefore
    /// cannot receive a new <see cref="Cnas.Ps.Core.Domain.ApplicationVersion"/> snapshot.
    /// Returned from <c>IApplicationVersionService.SaveAsync</c>; mapped to HTTP 409 by
    /// <c>ApplicationVersionsController</c>.
    /// </summary>
    public const string ApplicationNotEditable = "APPLICATION_NOT_EDITABLE";

    // --- Dossiers ---

    /// <summary>The dossier cannot transition into the requested state from its current state.</summary>
    public const string DossierInvalidTransition = "DOSSIER_INVALID_TRANSITION";

    // --- Workflow ---

    /// <summary>The workflow step cannot be approved because the actor lacks the decider role.</summary>
    public const string WorkflowNotDecider = "WORKFLOW_NOT_DECIDER";

    /// <summary>The workflow step is not currently assigned to this user.</summary>
    public const string WorkflowNotAssignee = "WORKFLOW_NOT_ASSIGNEE";

    /// <summary>
    /// The downstream BPMN workflow engine (Operaton / Camunda 7) returned a non-success
    /// status or could not be reached. Distinct from <see cref="Internal"/> so that
    /// operations dashboards can attribute incidents to the workflow subsystem.
    /// </summary>
    public const string WorkflowEngineFailed = "WORKFLOW_ENGINE_FAILED";

    /// <summary>
    /// R0574 / CF 08.06 — multi-level forward attempted from a state that is not on the
    /// approval chain (the application is not yet at <c>PendingApproval</c> or later).
    /// </summary>
    public const string WorkflowNotOnApprovalChain = "WORKFLOW_NOT_ON_APPROVAL_CHAIN";

    /// <summary>
    /// R0574 / CF 08.06 — multi-level forward attempted while already at the top of the
    /// approval chain (<c>ChiefCnas</c> / <c>ApplicationStatus.Approved</c>). The caller
    /// should use <see cref="WorkflowNotDecider"/>'s sibling <c>ApproveAsync</c> path
    /// instead.
    /// </summary>
    public const string WorkflowAlreadyAtTop = "WORKFLOW_ALREADY_AT_TOP";

    // --- File storage ---

    /// <summary>The uploaded file exceeds the configured maximum size.</summary>
    public const string FileTooLarge = "FILE_TOO_LARGE";

    /// <summary>The uploaded file's magic-byte signature does not match its declared content type.</summary>
    public const string FileTypeMismatch = "FILE_TYPE_MISMATCH";

    /// <summary>The requested file is not available for download (deleted, expired, or quarantined).</summary>
    public const string FileUnavailable = "FILE_UNAVAILABLE";

    /// <summary>
    /// R0137 — caller attempted to delete an object that carries an active
    /// application-level immutability stamp. Surfaced by
    /// <c>IFileImmutabilityGuard.CheckBeforeDeleteAsync</c>. Stable code documented
    /// here so external consumers can pattern-match on it without coupling to
    /// internal storage details.
    /// </summary>
    public const string ImmutableObject = "FILESTORAGE.IMMUTABLE_OBJECT";

    /// <summary>
    /// R0134 / CF 17.17 — A bulk catalog import (XML / CSV) failed because one or more
    /// rows did not pass validation. The import is all-or-nothing: any error aborts the
    /// whole batch before any row is persisted. The accompanying message carries a
    /// JSON-serialised report whose <c>Errors</c> list enumerates the failing rows.
    /// </summary>
    public const string ImportValidationFailed = "IMPORT_VALIDATION_FAILED";

    // --- External integrations (MGov) ---

    /// <summary>MPass authentication failed; the upstream provider rejected the assertion.</summary>
    public const string MPassFailed = "MPASS_FAILED";

    /// <summary>MSign signing failed; the signature service returned an error.</summary>
    public const string MSignFailed = "MSIGN_FAILED";

    /// <summary>MPay payment failed; the upstream payment service returned an error.</summary>
    public const string MPayFailed = "MPAY_FAILED";

    /// <summary>MConnect data exchange failed; the upstream interoperability service returned an error.</summary>
    public const string MConnectFailed = "MCONNECT_FAILED";

    /// <summary>
    /// MConnect partner-direct fallback path failed AFTER MConnect itself was unavailable.
    /// R0104 / TOR CF 14.03. Distinguishes the "both paths exhausted" failure from a plain
    /// <see cref="MConnectFailed"/> so operators can chart fallback effectiveness and so
    /// callers can surface a richer error message to the user (e.g. "the partner system is
    /// currently unreachable" rather than "MConnect failed").
    /// </summary>
    public const string MConnectFallbackFailed = "MCONNECT_FALLBACK_FAILED";

    /// <summary>MNotify dispatch failed; the upstream notification service returned an error.</summary>
    public const string MNotifyFailed = "MNOTIFY_FAILED";

    /// <summary>MLog dispatch failed; the upstream journaling service returned an error.</summary>
    public const string MLogFailed = "MLOG_FAILED";

    /// <summary>MPower verification failed; the upstream powers-of-attorney service returned an error.</summary>
    public const string MPowerFailed = "MPOWER_FAILED";

    /// <summary>
    /// MDocs managed-document call failed; the upstream document-storage service returned
    /// an error, timed out, or produced a malformed response. See
    /// <c>Cnas.Ps.Application.Abstractions.IMDocsClient</c> for the protocol details
    /// and <c>docs/EGOV-INTEGRATION-GAP.md</c> for the integration roadmap.
    /// </summary>
    public const string MDocsFailed = "MDOCS_FAILED";

    /// <summary>
    /// MCabinet citizen-portal publish failed; the upstream unified citizen dashboard
    /// (mcabinet.gov.md) returned a non-2xx status, was unreachable, or is not
    /// configured for this environment. Returned from
    /// <c>Cnas.Ps.Application.Abstractions.IMCabinetPublisher</c> for both publish
    /// and retire calls. The dossier state machine treats this as advisory — failing to
    /// mirror a state transition to MCabinet must not block the local transaction.
    /// </summary>
    public const string MCabinetPublishFailed = "MCABINET_PUBLISH_FAILED";

    /// <summary>
    /// R0117 / CF 14.11 / §2.5.5 — outbound publish to Portalul guvernamental de date (PGD)
    /// could not complete. Distinct from <see cref="MCabinetPublishFailed"/> so per-target
    /// dashboards / alerts can be configured independently — PGD is a public open-data
    /// portal while MCabinet is the citizen dashboard. Surfaced by
    /// <c>Cnas.Ps.Application.MessageBus.IPgdPublisher</c> for transport errors, non-2xx
    /// responses, AND when the base URL is not configured (the publisher refuses to make
    /// any HTTP call when <c>PgdPublisherOptions.BaseUrl</c> is blank).
    /// </summary>
    public const string PgdPublishFailed = "PGD_PUBLISH_FAILED";

    /// <summary>
    /// R0117 — the PGD publisher base URL is not configured in this environment, so the
    /// publisher cannot even attempt the upstream call. Distinct from
    /// <see cref="PgdPublishFailed"/> so operators can distinguish "we tried and the
    /// upstream said no" from "we never tried because the env is unconfigured" without
    /// parsing the failure message.
    /// </summary>
    public const string PgdNotConfigured = "PGD.NOT_CONFIGURED";

    /// <summary>
    /// MPower responded but no valid power of attorney exists for the (principal, delegate,
    /// service) tuple at the requested instant. The operator cannot submit on behalf of
    /// the principal until a delegation is registered (UC06 CF 06.02, R0551).
    /// </summary>
    public const string MPowerNotAuthorized = "MPOWER_NOT_AUTHORIZED";

    // --- Workflow task history (R0125) ---

    /// <summary>
    /// R0125 / CF 16.09 — the requested workflow task history projection could not be
    /// recorded or retrieved. Distinct from <see cref="NotFound"/> so callers can branch
    /// on the failure-to-persist case versus the "no such task" case.
    /// </summary>
    public const string WorkflowTaskHistoryFailed = "WORKFLOW_TASK_HISTORY_FAILED";

    // --- Template versioning (R0132) ---

    /// <summary>
    /// R0132 / CF 17.18 — the requested template version could not be located, or the
    /// supplied two-version diff/rollback refers to two different template codes (the
    /// caller passed a baseline from template A and a current from template B, which is
    /// not a meaningful diff). Surfaced by
    /// <c>Cnas.Ps.Application.Templates.ITemplateVersionHistoryService</c>.
    /// </summary>
    public const string TemplateVersionMismatch = "TEMPLATE_VERSION_MISMATCH";

    // --- Reporting ---

    /// <summary>The requested report exceeds resource budgets and was rejected.</summary>
    public const string ReportTooLarge = "REPORT_TOO_LARGE";

    // --- Value objects ---

    /// <summary>
    /// The supplied string is not a valid Moldovan IDNP (personal numeric code).
    /// IDNP must be 13 digits, start with 0/1/2, and satisfy the mod-10 weighted checksum.
    /// </summary>
    public const string InvalidIdnp = "INVALID_IDNP";

    /// <summary>
    /// The supplied string is not a valid Moldovan IDNO (organization numeric code).
    /// IDNO must be 13 digits, start with 1..9, and satisfy the mod-10 weighted checksum.
    /// </summary>
    public const string InvalidIdno = "INVALID_IDNO";

    /// <summary>
    /// The supplied currency code is not a recognised ISO-4217 code, or two
    /// <c>Money</c> operands have mismatched currencies.
    /// </summary>
    public const string InvalidMoneyCurrency = "INVALID_MONEY_CURRENCY";

    /// <summary>
    /// The supplied UTC date range is invalid (non-UTC <c>Kind</c>, or end &lt;= start).
    /// </summary>
    public const string InvalidDateRange = "INVALID_DATE_RANGE";

    /// <summary>
    /// The supplied percent rate is outside the inclusive range [0, 100].
    /// </summary>
    public const string InvalidPercentRate = "INVALID_PERCENT_RATE";

    /// <summary>
    /// The supplied phone number is not in E.164 form (after stripping whitespace/dashes/parens).
    /// </summary>
    public const string InvalidPhone = "INVALID_PHONE";

    /// <summary>
    /// The supplied IBAN is not a valid Moldovan IBAN (length, prefix, or mod-97 checksum failure).
    /// </summary>
    public const string InvalidIban = "INVALID_IBAN";

    // --- Decision engine ---

    /// <summary>
    /// A fact required by an eligibility or amount rule was not supplied to the decision engine.
    /// Surfaces from <see cref="Cnas.Ps.Core.Common"/>-aware engine code when the
    /// <c>DecisionFacts</c> dictionary is missing the requested key.
    /// </summary>
    public const string MissingFact = "MISSING_FACT";

    /// <summary>
    /// The declarative rule-set JSON is malformed, references an unknown rule kind, or
    /// contains a value whose runtime type does not match the rule contract
    /// (e.g. comparing a string fact with a numeric rule). Indicates a passport
    /// configuration bug rather than a runtime user-data issue.
    /// </summary>
    public const string BadRule = "BAD_RULE";

    /// <summary>
    /// The applicant is not eligible for the service per the declarative rule-set. The
    /// human-readable reason codes (e.g. <c>INELIGIBLE_NOT_INSURED</c>) are returned on
    /// the <c>DecisionOutcome.ReasonCodes</c> collection.
    /// </summary>
    public const string Ineligible = "INELIGIBLE";

    /// <summary>
    /// Eligibility passed but the amount-computation step failed (unknown amount kind,
    /// missing currency, divide-by-zero, etc.). Indicates a passport configuration
    /// problem worth alerting administrators about.
    /// </summary>
    public const string AmountComputationFailed = "AMOUNT_COMPUTATION_FAILED";

    // --- Secrets management ---

    /// <summary>
    /// The requested secret key is not present in the configured secrets backend
    /// (e.g. no matching environment variable, or Vault returned 404 for the path).
    /// Returned from <c>Cnas.Ps.Application.Abstractions.ISecretsProvider</c>.
    /// </summary>
    public const string SecretNotFound = "SECRET_NOT_FOUND";

    /// <summary>
    /// The secrets backend could not be reached or returned a server-side error
    /// (network failure, timeout, HTTP 5xx, sealed Vault, ...). Distinct from
    /// <see cref="SecretNotFound"/> so that operations dashboards can attribute
    /// incidents to the secrets subsystem and so the application can fall back to
    /// safe defaults when secrets are temporarily unavailable.
    /// </summary>
    public const string SecretsBackendUnavailable = "SECRETS_BACKEND_UNAVAILABLE";

    // --- DOCX templates (Annex 7) ---

    /// <summary>
    /// A required fact for an Annex 7 DOCX template was missing from the supplied facts
    /// dictionary. Returned from
    /// <see cref="Cnas.Ps.Core.Common"/>-aware code in
    /// <c>Cnas.Ps.Infrastructure.Documents.Templates.IDocxTemplate</c> implementations
    /// when a per-template required key (e.g. <c>beneficiaryFullName</c>) is not provided
    /// by the caller. Distinct from <see cref="MissingFact"/> (decision-engine fact) so
    /// that operations dashboards can attribute incidents to the document-rendering
    /// subsystem.
    /// </summary>
    public const string TemplateMissingFacts = "TEMPLATE_MISSING_FACTS";

    /// <summary>
    /// R0131 / CF 17.15 — a metadata-driven validation rule fired against the form values
    /// supplied to a template render. Returned from
    /// <c>Cnas.Ps.Application.Templates.ITemplateValidationService.Validate</c>; the
    /// human-readable message names the offending <c>FieldName</c> and rule kind so the
    /// UI can highlight the input. Distinct from
    /// <see cref="ValidationFailed"/> so dashboards can attribute incidents to the
    /// template-validation subsystem specifically.
    /// </summary>
    public const string TemplateValidationFailed = "TEMPLATE_VALIDATION_FAILED";

    /// <summary>
    /// R0143 / CF 17.19 — a calc-formula expression supplied through
    /// <c>ServicePassport.CalcFormulasJson</c> failed to parse or evaluate. Returned from
    /// <c>Cnas.Ps.Application.Calculations.IExpressionEvaluator.Evaluate</c>; the human-
    /// readable message names the offending token / input. Distinct from
    /// <see cref="BadRule"/> (decision-engine) so dashboards can attribute incidents to
    /// the calc-formula subsystem specifically.
    /// </summary>
    public const string ExpressionInvalid = "EXPRESSION_INVALID";

    /// <summary>
    /// R0143 / CF 17.19 — a calc-formula expression referenced a named input that was not
    /// supplied to the evaluator. Returned from
    /// <c>Cnas.Ps.Application.Calculations.IExpressionEvaluator.Evaluate</c> when the
    /// inputs dictionary lacks a key referenced by the expression. Distinct from
    /// <see cref="MissingFact"/> (decision-engine fact) so dashboards can attribute
    /// incidents to the calc-formula subsystem specifically.
    /// </summary>
    public const string ExpressionUnknownInput = "EXPRESSION_UNKNOWN_INPUT";

    /// <summary>
    /// R0143 / CF 17.19 — a calc-formula expression attempted to divide by zero (or by a
    /// value that evaluated to zero). Returned from
    /// <c>Cnas.Ps.Application.Calculations.IExpressionEvaluator.Evaluate</c>.
    /// </summary>
    public const string ExpressionDivideByZero = "EXPRESSION_DIVIDE_BY_ZERO";

    // --- Authentication / passwords ---

    /// <summary>
    /// The supplied password fails one or more rules of the local-login password policy
    /// (CLAUDE.md §5.3 / TOR SEC 014 / R0052): minimum 8 / maximum 128 characters, at
    /// least one lowercase, one uppercase, one digit, and one symbol. Returned from
    /// <c>Cnas.Ps.Application.Validators.PasswordPolicyValidator</c>. A single code
    /// covers every individual rule failure — the human-readable message attached to
    /// the validation result names the specific rule that fired so the UI can render a
    /// useful prompt without callers having to branch on multiple stable codes.
    /// </summary>
    public const string PasswordPolicyViolation = "PASSWORD_POLICY_VIOLATION";

    // --- mTLS / client certificates (MGov universal transport hardening) ---

    /// <summary>
    /// No client certificate is registered for the requested MGov service. Returned from
    /// <c>Cnas.Ps.Application.Abstractions.ICertificateStore.GetCertificate</c> when the
    /// service name is absent from the <c>Cnas:MGov:Mtls:Certificates</c> configuration
    /// section. Callers that allow Bearer-token fallback should prefer
    /// <c>TryGetCertificate</c>, which returns a successful <c>null</c> result for the
    /// same case rather than this failure.
    /// </summary>
    public const string CertificateNotConfigured = "CERTIFICATE_NOT_CONFIGURED";

    /// <summary>
    /// A client certificate was loaded from disk but its SHA-1 thumbprint does not match
    /// the value pinned in configuration. Indicates either an unintended file swap
    /// (deployment mistake) or active tampering — the certificate is rejected and the
    /// caller must treat the MGov service as unavailable until operators investigate.
    /// </summary>
    public const string CertificateThumbprintMismatch = "CERTIFICATE_THUMBPRINT_MISMATCH";

    /// <summary>
    /// Loading the configured PFX / PKCS#12 file failed (file not found, wrong password,
    /// corrupt content, unsupported key type, ...). Distinct from
    /// <see cref="CertificateNotConfigured"/> so that operations dashboards can
    /// attribute incidents to a deployment or rotation issue rather than to missing
    /// configuration.
    /// </summary>
    public const string CertificateLoadFailed = "CERTIFICATE_LOAD_FAILED";

    // --- Maker-checker / 4-eyes admin actions (R0058 / SEC 027) ---

    /// <summary>
    /// The administrator attempting to approve or reject a pending admin action is the
    /// same user that submitted it. The 4-eyes principle requires a second, independent
    /// administrator — the maker cannot be the checker. Returned from
    /// <c>Cnas.Ps.Application.UseCases.IPendingAdminActionService.ApproveAsync</c>
    /// and <c>RejectAsync</c>; mapped to HTTP 403 by
    /// <c>PendingAdminActionsController</c>.
    /// </summary>
    public const string MakerCheckerSelfApprovalForbidden = "MAKER_CHECKER_SELF_APPROVAL_FORBIDDEN";

    /// <summary>
    /// The pending admin action has already transitioned out of
    /// <c>PendingAdminActionStatus.Pending</c> — it was previously approved, rejected,
    /// or auto-expired. Distinct from <see cref="Conflict"/> so callers can branch on
    /// the idempotency guard specifically (a UI that hits approve twice rapidly should
    /// surface a friendly "already decided" message rather than a generic conflict).
    /// Mapped to HTTP 409 by <c>PendingAdminActionsController</c>.
    /// </summary>
    public const string MakerCheckerAlreadyDecided = "MAKER_CHECKER_ALREADY_DECIDED";

    /// <summary>
    /// The pending admin action's TTL elapsed before the checker decided. The service
    /// flips <c>Status</c> to <c>Expired</c> on the offending read and returns this
    /// code so the UI can prompt the maker to resubmit if still relevant. Mapped to
    /// HTTP 409 by <c>PendingAdminActionsController</c>.
    /// </summary>
    public const string MakerCheckerExpired = "MAKER_CHECKER_EXPIRED";

    /// <summary>
    /// The submitted operation code is not registered with any
    /// <c>Cnas.Ps.Application.UseCases.IPendingAdminActionExecutor</c>. Caught at
    /// submit time (fail-fast) rather than approve time so a maker cannot enqueue an
    /// undeliverable action. Mapped to HTTP 400 by <c>PendingAdminActionsController</c>.
    /// </summary>
    public const string MakerCheckerUnknownOperation = "MAKER_CHECKER_UNKNOWN_OPERATION";

    // --- Generic 4-eyes admin substrate (R2273 / SEC 027) ---

    /// <summary>
    /// The operator attempting to approve or reject a
    /// <c>Cnas.Ps.Core.Domain.SensitiveAdminAction</c> is the SAME operator that opened
    /// the request. The 4-eyes principle requires a second, independent operator —
    /// the requester cannot be the approver. Returned from
    /// <c>Cnas.Ps.Application.SensitiveActions.ISensitiveAdminActionService.ApproveAsync</c>
    /// and <c>RejectAsync</c>; mapped to HTTP 409 by
    /// <c>SensitiveAdminActionsController</c>.
    /// </summary>
    public const string FourEyesSameOperator = "FOUR_EYES.SAME_OPERATOR";

    /// <summary>
    /// The targeted <c>Cnas.Ps.Core.Domain.SensitiveAdminAction</c> is no longer in
    /// <c>SensitiveAdminActionStatus.PendingApproval</c> — it was previously approved,
    /// rejected, cancelled, or auto-expired. Distinct from <see cref="Conflict"/> so
    /// callers can branch on the idempotency guard specifically. Mapped to HTTP 409 by
    /// <c>SensitiveAdminActionsController</c>.
    /// </summary>
    public const string FourEyesAlreadyDecided = "FOUR_EYES.ALREADY_DECIDED";

    /// <summary>
    /// The submitted <c>ActionCode</c> is not registered with any
    /// <c>Cnas.Ps.Application.SensitiveActions.ISensitiveActionPolicy</c>. Caught at
    /// request time (fail-fast). Mapped to HTTP 400 by
    /// <c>SensitiveAdminActionsController</c>.
    /// </summary>
    public const string FourEyesUnknownAction = "FOUR_EYES.UNKNOWN_ACTION";

    // --- User account state machine (R0059 / SEC 016) ---

    /// <summary>
    /// The requested transition between
    /// <c>Cnas.Ps.Core.Domain.UserAccountState</c> values is not permitted by the
    /// state machine (e.g. <c>Disabled → Locked</c> or any transition from
    /// <c>Active → Active</c> no-op). Returned from
    /// <c>Cnas.Ps.Application.UseCases.IUserAccountStateService.ChangeStateAsync</c>
    /// and the auto-lock path
    /// <c>IUserAccountStateService.LockForFailedLoginsAsync</c> when the user is
    /// already in a non-Active terminal-style state (e.g. Disabled). Mapped to
    /// HTTP 409 by <c>UsersController.ChangeStateAsync</c>.
    /// </summary>
    public const string UserAccountStateTransitionForbidden = "USER_ACCOUNT_STATE_TRANSITION_FORBIDDEN";

    /// <summary>
    /// R0672 / TOR CF 18.08 — a user-profile soft-delete (deactivation) was
    /// refused because the underlying row carries neither an
    /// <c>AuditLog</c> nor an <c>EntityHistoryRow</c> entry. The policy
    /// guarantees that every deactivation leaves at least one trail row
    /// behind on the user — brand-new accounts that have done nothing
    /// auditable yet cannot be silently soft-deleted before any traceability
    /// landed. Returned from
    /// <c>Cnas.Ps.Application.Users.IUserDeactivationGuard.EnsureCanDeactivateAsync</c>
    /// and surfaced from
    /// <c>Cnas.Ps.Application.UseCases.IUserAdministrationService.DeactivateAsync</c>.
    /// Mapped to HTTP 409 by <c>UsersController</c>.
    /// </summary>
    public const string UserProfileNoAuditHistory = "USERPROFILE.NO_AUDIT_HISTORY";

    // --- CAPTCHA / abuse prevention (R0035) ---

    /// <summary>
    /// The anonymous request reached a <c>[RequireCaptcha]</c>-protected endpoint
    /// (UC01 / UC02 public surface) without supplying a CAPTCHA token via the
    /// <c>X-Captcha-Token</c> header. Distinct from <see cref="CaptchaTokenInvalid"/>
    /// because a missing token is a client-side wiring bug (or scripted abuse) rather
    /// than a provider rejection — the UI should re-render the challenge widget rather
    /// than alert the user. Mapped to HTTP 400 by <c>RequireCaptchaAttribute</c>.
    /// </summary>
    public const string CaptchaTokenMissing = "CAPTCHA_TOKEN_MISSING";

    /// <summary>
    /// The CAPTCHA provider (Cloudflare Turnstile) rejected the supplied token. Indicates
    /// either a stale challenge response, an attempt to replay a previously consumed
    /// token, or a non-human caller. The provider-supplied error code list is appended
    /// to the human-readable failure message but the raw token is NEVER logged or
    /// echoed back. Mapped to HTTP 400 by <c>RequireCaptchaAttribute</c>.
    /// </summary>
    public const string CaptchaTokenInvalid = "CAPTCHA_TOKEN_INVALID";

    /// <summary>
    /// The CAPTCHA provider's <c>siteverify</c> endpoint could not be reached (network
    /// failure, timeout, HTTP 5xx, or malformed response). The gateway fails CLOSED —
    /// a degraded CAPTCHA service must NOT become an open door — so this code surfaces
    /// as HTTP 503 (Service Unavailable) at the wire. Operations dashboards should
    /// attribute incidents tagged with this code to the abuse-prevention subsystem
    /// rather than to a generic upstream failure.
    /// </summary>
    public const string CaptchaProviderUnreachable = "CAPTCHA_PROVIDER_UNREACHABLE";

    /// <summary>
    /// R0507 / TOR CF 01.10 — a verified CAPTCHA token was presented to the
    /// downstream gated endpoint but the token's one-shot allowance has
    /// already been consumed by a prior request inside the post-verify
    /// window. Distinct from <see cref="CaptchaTokenInvalid"/> so operators
    /// can attribute incidents tagged with this code to replay-after-success
    /// attempts (legitimate clients should never see this — they re-mint a
    /// fresh challenge per request rather than reusing a verified token).
    /// Returned from <c>Cnas.Ps.Application.Captcha.ICaptchaChallengeService.ConsumeAsync</c>.
    /// </summary>
    public const string CaptchaAlreadyConsumed = "CAPTCHA.ALREADY_CONSUMED";

    // --- MPass SAML (assertion parsing) ---

    /// <summary>
    /// The supplied SAML 2.0 assertion XML is structurally invalid — the document is not
    /// well-formed, the root element is not <c>&lt;saml:Assertion&gt;</c>, or a required
    /// structural piece (Conditions, AttributeStatement) is missing. Distinct from
    /// <see cref="MPassFailed"/> so that operations dashboards can attribute incidents
    /// to a payload-shape problem rather than to a generic upstream failure. Returned
    /// from <c>Cnas.Ps.Application.Abstractions.ISamlAssertionParser.Parse</c>.
    /// </summary>
    public const string InvalidSaml = "INVALID_SAML";

    /// <summary>
    /// The SAML assertion's validity window (<c>NotBefore</c> / <c>NotOnOrAfter</c>) does
    /// not include the current UTC instant once <c>ClockSkew</c>-equivalent
    /// tolerance is applied. Indicates an expired token (most common — the user took too
    /// long between login and ACS POST) or a clock-skewed Identity Provider (rare — but
    /// the symmetric tolerance covers both edges). Returned from
    /// <c>Cnas.Ps.Application.Abstractions.ISamlAssertionParser.Parse</c>.
    /// </summary>
    public const string SamlAssertionExpired = "SAML_ASSERTION_EXPIRED";

    /// <summary>
    /// The SAML assertion's <c>&lt;AudienceRestriction&gt;</c> does not list the CNAS
    /// service-provider entity id, or the <c>AudienceRestriction</c> element is missing
    /// entirely. Indicates a misrouted assertion (someone else's relying party) — the
    /// caller must reject the sign-in and instruct the user to retry. Returned from
    /// <c>Cnas.Ps.Application.Abstractions.ISamlAssertionParser.Parse</c>.
    /// </summary>
    public const string SamlAssertionAudienceMismatch = "SAML_ASSERTION_AUDIENCE_MISMATCH";

    // --- Refresh-token lifecycle (R0053 / SEC 018) ---

    /// <summary>
    /// The token-issue request omitted the required <c>refreshToken</c> field. Distinct
    /// from <see cref="RefreshTokenInvalid"/> so callers can distinguish "the client
    /// forgot to send the token" (client-side wiring bug — surfaces as HTTP 400) from
    /// "the token is unrecognised" (HTTP 401). Returned from the
    /// <c>POST /api/auth/token</c> controller path when <c>grantType=refresh_token</c>
    /// arrives without a body token.
    /// </summary>
    public const string RefreshTokenMissing = "REFRESH_TOKEN_MISSING";

    /// <summary>
    /// The supplied refresh-token plaintext is not recognised (wrong format, never
    /// issued, or already cleaned up). Mapped to HTTP 401 by the token controller —
    /// from the client's perspective an invalid token is indistinguishable from an
    /// unauthorised one. Returned from
    /// <c>Cnas.Ps.Application.Abstractions.IRefreshTokenService.RotateAsync</c>.
    /// </summary>
    public const string RefreshTokenInvalid = "REFRESH_TOKEN_INVALID";

    /// <summary>
    /// The refresh token was found but its <c>ExpiresAtUtc</c> has elapsed. The token
    /// is dead — the caller must re-authenticate via the primary login flow. Mapped
    /// to HTTP 401. Returned from
    /// <c>Cnas.Ps.Application.Abstractions.IRefreshTokenService.RotateAsync</c>.
    /// </summary>
    public const string RefreshTokenExpired = "REFRESH_TOKEN_EXPIRED";

    /// <summary>
    /// The refresh token was found but is already revoked — either because the family
    /// was logged out, the row was revoked by an admin, or the underlying user account
    /// is no longer in <see cref="Cnas.Ps.Core.Domain.UserAccountState.Active"/>.
    /// Distinct from <see cref="RefreshTokenReused"/>, which fires reuse-detection.
    /// Mapped to HTTP 401.
    /// </summary>
    public const string RefreshTokenRevoked = "REFRESH_TOKEN_REVOKED";

    /// <summary>
    /// The presented refresh token was already consumed by a previous rotation — a
    /// classic stolen-credential signal. The service revokes EVERY live token in the
    /// same family before returning this code so both the legitimate user and the
    /// attacker lose access simultaneously. Mapped to HTTP 401 by the token
    /// controller; logged at WARNING level on the service side with the numeric user
    /// id + family id (no PII). Returned from
    /// <c>Cnas.Ps.Application.Abstractions.IRefreshTokenService.RotateAsync</c>.
    /// </summary>
    public const string RefreshTokenReused = "REFRESH_TOKEN_REUSED";

    // --- Bulk actions (R0166 / CF 03.11 / UI 015) ---

    /// <summary>
    /// The submitted bulk-operation code is not registered with any
    /// <c>Cnas.Ps.Application.BulkActions.IBulkOperation</c>. Returned from
    /// <c>IBulkOperationRunner.RunAsync</c> when the dispatch table lookup misses;
    /// mapped to HTTP 404 by <c>BulkActionsController</c>. Distinct from
    /// <see cref="NotFound"/> so a UI can render a specific "operation not available"
    /// prompt rather than a generic 404.
    /// </summary>
    public const string BulkOperationUnknown = "BULK_OP_UNKNOWN";

    /// <summary>
    /// The resolved row set exceeds the operation's <c>MaxRowsPerRun</c> quota. Returned
    /// from <c>IBulkOperationRunner.RunAsync</c>; the caller must narrow the selection
    /// (tighten the filter or extend the exclude list) and submit a fresh run. Mapped
    /// to HTTP 400 by <c>BulkActionsController</c>.
    /// </summary>
    public const string BulkQuotaExceeded = "QUOTA_EXCEEDED";

    /// <summary>
    /// The bulk selection has already been consumed by a prior <c>BulkOperationRun</c>.
    /// A second consumption attempt is forbidden by design — bulk operations are
    /// high-blast-radius admin actions and re-running against a stale row set must
    /// require an explicit fresh selection. Mapped to HTTP 409 by
    /// <c>BulkActionsController</c>.
    /// </summary>
    public const string BulkSelectionConsumed = "BULK_SELECTION_CONSUMED";

    /// <summary>
    /// The bulk selection has expired (the <c>ExpiresAtUtc</c> instant has passed).
    /// The caller must create a fresh selection. Mapped to HTTP 409 by
    /// <c>BulkActionsController</c>.
    /// </summary>
    public const string BulkSelectionExpired = "BULK_SELECTION_EXPIRED";

    /// <summary>
    /// The supplied external id (Sqid) is well-formed but the underlying row does not
    /// exist or has been soft-deleted. Used by bulk-action validators to flag
    /// individual entries in <c>ExplicitIncludeIds</c> / <c>ExplicitExcludeIds</c>
    /// that decode but reference unknown rows. Mapped to HTTP 400 by
    /// <c>BulkActionsController</c>.
    /// </summary>
    public const string InvalidId = "INVALID_ID";

    // --- Saved searches (R0165 / CF 03.06) ---

    /// <summary>
    /// The owner already holds the maximum number of saved searches permitted by
    /// <c>SavedSearchOptions.MaxPerOwner</c> (default 50). The caller must delete one
    /// before persisting another. Returned from
    /// <c>Cnas.Ps.Application.UseCases.ISavedSearchService.CreateAsync</c> and mapped to
    /// HTTP 400 by <c>SavedSearchesController</c>. Distinct from
    /// <see cref="ValidationFailed"/> so a UI can surface a specific "too many saved
    /// searches — delete one to continue" prompt rather than a generic validation error.
    /// </summary>
    public const string SavedSearchLimitReached = "SAVED_SEARCH_LIMIT_REACHED";

    // --- Query budget / result narrowing (R0167 / CF 01.06 / CF 03.07-08) ---

    /// <summary>
    /// The filtered list query would materialise more rows than the registry's
    /// configured budget allows. Returned from a registry list service (e.g.
    /// <c>ISolicitantService.ListAsync</c>) and mapped to HTTP 422 by the controller
    /// with a structured refinement-prompt ProblemDetails carrying the
    /// <c>QueryBudgetVerdictDto</c> in the <c>extensions["budget"]</c> slot. Distinct
    /// from <see cref="ValidationFailed"/> so the UI can render a specific
    /// "narrow your filter" prompt instead of a generic validation error.
    /// </summary>
    public const string QueryTooBroad = "QUERY_TOO_BROAD";

    // --- Universal grid export (R0226 / TOR UI 013) ---

    /// <summary>
    /// R0226 / TOR UI 013 — the universal grid export was asked to render more rows
    /// than the configured per-call cap (<c>MaxExportRows</c>, default 50 000).
    /// Returned from <c>Cnas.Ps.Application.Exports.IGridExporter.ExportAsync</c>
    /// before the renderer is ever invoked so a very large list cannot exhaust
    /// memory or block a request thread. Mapped to HTTP 422 by
    /// <c>GridExportsController</c> with the row count carried in
    /// <c>extensions["rowCount"]</c> so the UI can prompt the user to narrow the
    /// filter before retrying. Distinct from <see cref="QueryTooBroad"/> — that
    /// fires UPSTREAM (the SQL count over the filtered query exceeds the registry
    /// budget); this fires DOWNSTREAM (the filter passed the budget guard but the
    /// resulting payload would still be too large to render).
    /// </summary>
    public const string ExportTooLarge = "EXPORT_TOO_LARGE";

    /// <summary>
    /// R0226 / TOR UI 013 — the universal grid export was asked for a format whose
    /// renderer is not wired up (or is in a placeholder state in this environment).
    /// Returned from
    /// <c>Cnas.Ps.Application.Exports.IGridExporter.ExportAsync</c> when no
    /// <c>IGridExportRenderer</c> is registered for the requested
    /// <c>Cnas.Ps.Application.Exports.ExportFormat</c>. Mapped to HTTP 501 by
    /// <c>GridExportsController</c> with the format name carried in
    /// <c>extensions["format"]</c> so the UI can hide the corresponding download
    /// button without re-issuing the request.
    /// </summary>
    public const string ExportFormatNotSupported = "EXPORT_FORMAT_NOT_SUPPORTED";

    /// <summary>
    /// R0529 / TOR CF 03.14 — the DOCX export pipeline is intentionally
    /// degraded on this build. Returned by the DOCX implementation of
    /// <c>Cnas.Ps.Application.Reporting.IReportExporter</c> when the
    /// <c>DocumentFormat.OpenXml</c> NuGet package is not loaded (e.g.
    /// trimmed deployments). Mapped to HTTP 501 by the report-export
    /// controller with the format name in the ProblemDetails extension
    /// bag. Distinct from <see cref="ExportFormatNotSupported"/> so the
    /// dashboard can attribute "DOCX missing" specifically rather than
    /// "no exporter registered at all".
    /// </summary>
    public const string ExportDocxNotAvailable = "EXPORT.DOCX_NOT_AVAILABLE";

    // --- Profile updates / refresh (R0362 / R0363 / UC13) ---

    /// <summary>
    /// R0362 — the submitted profile-update request type string did not parse to any
    /// member of <c>ProfileUpdateRequestType</c>. Returned from the submit-side
    /// validator/service before any row is written. Mapped to HTTP 400.
    /// </summary>
    public const string ProfileUpdateUnknownType = "PROFILE_UPDATE_UNKNOWN_TYPE";

    /// <summary>
    /// R0362 — the supplied <c>RequestedChangesJson</c> failed to parse to the input DTO
    /// shape required by the request <c>Type</c> at apply time. The caller must correct
    /// the JSON and submit a new request. Mapped to HTTP 400 (or to 422 when surfaced as
    /// a per-request apply failure recorded on the row).
    /// </summary>
    public const string ProfileUpdateInvalidPayload = "PROFILE_UPDATE_INVALID_PAYLOAD";

    /// <summary>
    /// R0363 — the requested external-data source code is not one of <c>RSP</c>,
    /// <c>RSUD</c>, or <c>SI_SFS</c>. Returned from
    /// <c>IProfileRefreshService.RefreshFromSourceAsync</c> before any row is written.
    /// Mapped to HTTP 400.
    /// </summary>
    public const string ProfileRefreshUnknownSource = "PROFILE_REFRESH_UNKNOWN_SOURCE";

    // --- QBE (Query-By-Example) primitive (R0163 / TOR UI 009) ---

    /// <summary>
    /// R0163 — the supplied <c>QbeFilter</c> targets a registry code that is not
    /// registered with the underlying <c>IQbeRegistrySchemaProvider</c>. Returned from
    /// <c>IQbeToLinqConverter.Convert</c>; mapped to HTTP 400 by registry list endpoints
    /// that accept a QBE envelope. Distinct from <see cref="NotFound"/> so the UI can
    /// surface a "this grid does not support QBE" prompt rather than a generic 404.
    /// </summary>
    public const string QbeRegistryUnknown = "QBE_REGISTRY_UNKNOWN";

    /// <summary>
    /// R0163 — a <c>QbeCondition.FieldName</c> does not appear in the target
    /// registry's allow-list of queryable fields. Returned from
    /// <c>IQbeToLinqConverter.Convert</c>; mapped to HTTP 400 with the offending
    /// field carried in <c>extensions["fieldName"]</c> so the UI can highlight the
    /// invalid row in the QBE form.
    /// </summary>
    public const string QbeFieldNotQueryable = "QBE_FIELD_NOT_QUERYABLE";

    /// <summary>
    /// R0163 — the requested <c>QbeOperator</c> is not valid for the field's declared
    /// type (e.g. <c>Between</c> on a <c>bool</c> field). Returned from
    /// <c>IQbeToLinqConverter.Convert</c>; mapped to HTTP 400.
    /// </summary>
    public const string QbeOperatorNotSupported = "QBE_OPERATOR_NOT_SUPPORTED";

    /// <summary>
    /// R0163 — the <c>QbeCondition.Value</c> (or <c>Value2</c>) could not be parsed
    /// against the field's declared type (e.g. <c>"abc"</c> supplied for a
    /// <see cref="DateTime"/> field, or <c>Between</c> with missing <c>Value2</c>).
    /// Returned from <c>IQbeToLinqConverter.Convert</c>; mapped to HTTP 400.
    /// </summary>
    public const string QbeValueInvalid = "QBE_VALUE_INVALID";

    /// <summary>
    /// R0163 — the <c>QbeFilter.Combinator</c> is not one of the canonical
    /// <c>"AND"</c> / <c>"OR"</c> literals. Returned from
    /// <c>IQbeToLinqConverter.Convert</c>; mapped to HTTP 400.
    /// </summary>
    public const string QbeInvalidCombinator = "QBE_INVALID_COMBINATOR";

    // --- Access-scope back-fill helper (R0671 continuation / CF 18.06) ---

    /// <summary>
    /// R0671 continuation — the back-fill helper refused the call because the
    /// resolved row set exceeded the per-call cap (5000 rows). Returned from
    /// <c>Cnas.Ps.Application.AccessScope.IAccessScopeBackfillService</c>; mapped
    /// to HTTP 400 by <c>AccessScopeBackfillController</c> with the unbounded count
    /// carried in <c>extensions["rowCount"]</c> so the UI can prompt the operator
    /// to narrow the filter before retrying. Distinct from
    /// <see cref="BulkQuotaExceeded"/> — that fires on the bulk-action subsystem;
    /// this fires on the back-fill helper which uses a different audit + tagging
    /// envelope.
    /// </summary>
    public const string BackfillQuotaExceeded = "BACKFILL_QUOTA_EXCEEDED";

    /// <summary>
    /// R0671 continuation — the back-fill helper was given a
    /// <c>SubdivisionCode</c> that does not match any active row of
    /// <see cref="Cnas.Ps.Core.Domain.CnasBranch.Code"/>. Returned from
    /// <c>IAccessScopeBackfillService.AssignServiceApplicationSubdivisionByPatternAsync</c>;
    /// mapped to HTTP 400 by the controller (distinct human message from a
    /// generic <see cref="NotFound"/> so the operator sees "unknown branch code"
    /// rather than a 404 sweep).
    /// </summary>
    public const string BranchNotFound = "BRANCH_NOT_FOUND";

    // --- Mass recalculation (R1503 / TOR §3.7-D) ---

    /// <summary>
    /// R1503 — the mass-recalculation engine refused to start because the
    /// peak-hour gate signalled SKIP. The caller may retry during the
    /// off-peak window or override the gate from the admin surface. Mapped
    /// to HTTP 409.
    /// </summary>
    public const string PeakHourBlocked = "PEAK_HOUR_BLOCKED";

    /// <summary>
    /// R1503 — no <c>IBenefitRecalculationStrategy</c> is registered for the
    /// scoped benefit kind. The orchestrator tags every affected decision row
    /// <see cref="Cnas.Ps.Core.Domain.RecalculationResultStatus.Skipped"/>
    /// with this reason so the operator can plan strategy onboarding.
    /// </summary>
    public const string NoStrategyRegistered = "NO_STRATEGY_REGISTERED";

    // --- ABAC (R2271 / SEC 025) ---

    /// <summary>
    /// R2271 / SEC 025 — the submitted ABAC condition expression failed to parse
    /// against the safe sub-language grammar. Returned from
    /// <c>IAbacExpressionParser.Parse</c> (typically through a validator) and
    /// from <c>IAbacRuleRegistryService.AddRuleAsync</c> /
    /// <c>ModifyRuleAsync</c> when an administrator supplies a malformed rule.
    /// Mapped to HTTP 400 by <c>AbacAdminController</c>. Distinct from
    /// <see cref="ValidationFailed"/> so the admin UI can highlight the
    /// expression editor with a parse-specific message.
    /// </summary>
    public const string AbacParseError = "ABAC.PARSE_ERROR";

    /// <summary>
    /// R2271 / SEC 025 — the targeted <see cref="Cnas.Ps.Core.Domain.AbacRuleSet"/>
    /// or <see cref="Cnas.Ps.Core.Domain.AbacRule"/> could not be resolved.
    /// Returned from <c>IAbacRuleRegistryService</c> get/list/modify paths.
    /// Mapped to HTTP 404.
    /// </summary>
    public const string AbacNotFound = "ABAC.NOT_FOUND";

    /// <summary>
    /// R2271 / SEC 025 — the submitted policy name already belongs to another
    /// active <see cref="Cnas.Ps.Core.Domain.AbacRuleSet"/>. Policy names are
    /// the contract surface that <c>[AbacPolicy("…")]</c> references — duplicates
    /// are rejected at create time. Mapped to HTTP 409.
    /// </summary>
    public const string AbacDuplicatePolicyName = "ABAC.DUPLICATE_POLICY_NAME";

    // --- Service management (R2501-R2504 / TOR PIR 024-025) ---

    /// <summary>
    /// R2501 / TOR PIR 024 — the referenced
    /// <see cref="Cnas.Ps.Core.Domain.BusinessHoursPolicy"/> was not found by
    /// the provided code or Sqid. Mapped to HTTP 404 by the admin controllers.
    /// </summary>
    public const string BusinessHoursPolicyNotFound = "BUSINESS_HOURS.POLICY_NOT_FOUND";

    /// <summary>
    /// R2501 / TOR PIR 024 — a policy with the same
    /// <see cref="Cnas.Ps.Core.Domain.BusinessHoursPolicy.Code"/> already
    /// exists. Mapped to HTTP 409.
    /// </summary>
    public const string BusinessHoursPolicyDuplicateCode = "BUSINESS_HOURS.DUPLICATE_CODE";

    /// <summary>
    /// R2502 / TOR PIR 025 — the requested state transition is not legal for
    /// the current <see cref="Cnas.Ps.Core.Domain.MaintenanceWindowStatus"/>.
    /// Mapped to HTTP 409.
    /// </summary>
    public const string MaintenanceInvalidTransition = "MAINT.INVALID_TRANSITION";

    /// <summary>
    /// R2502 / TOR PIR 025 — the proposed window duration exceeds the
    /// kind-specific ceiling (Ordinary ≤ 4h, Major ≤ 24h, Urgent ≤ 2h).
    /// Mapped to HTTP 400 by the admin controller.
    /// </summary>
    public const string MaintenanceDurationExceeded = "MAINT.DURATION_EXCEEDED";

    /// <summary>
    /// R2502 / TOR PIR 025 — the advance-notice lead time is insufficient
    /// for the window's kind (Ordinary ≥ 5 business days, Major ≥ 10
    /// business days). Mapped to HTTP 409.
    /// </summary>
    public const string MaintenanceNoticeLeadTimeInsufficient = "MAINT.NOTICE_LEAD_TIME_INSUFFICIENT";

    /// <summary>
    /// R2503 / TOR PIR 022-023 — a schedule with the same
    /// <see cref="Cnas.Ps.Core.Domain.SystemUpdateSchedule.ScheduleCode"/>
    /// already exists. Mapped to HTTP 409.
    /// </summary>
    public const string UpdateScheduleDuplicateCode = "UPDATE.SCHEDULE.DUPLICATE_CODE";

    /// <summary>
    /// R2504 / TOR PIR 024 — the requested state transition is not legal
    /// for the current
    /// <see cref="Cnas.Ps.Core.Domain.SystemUpdateEventStatus"/>. Mapped to
    /// HTTP 409.
    /// </summary>
    public const string UpdateEventInvalidTransition = "UPDATE.INVALID_TRANSITION";

    /// <summary>
    /// R2504 / TOR PIR 024 — the planned deployment is closer to "now" than
    /// the parent schedule's
    /// <see cref="Cnas.Ps.Core.Domain.SystemUpdateSchedule.NoticeLeadTimeDays"/>
    /// requires. Mapped to HTTP 409.
    /// </summary>
    public const string UpdateEventLeadTimeInsufficient = "UPDATE.LEAD_TIME_INSUFFICIENT";

    /// <summary>
    /// R2505 / TOR PIR 030-033 — the requested state transition is not legal
    /// for the current
    /// <see cref="Cnas.Ps.Core.Domain.ChangeRequestStatus"/>. Mapped to HTTP 409.
    /// </summary>
    public const string ChangeRequestInvalidTransition = "CHG.INVALID_TRANSITION";

    /// <summary>
    /// R2505 / TOR PIR 030-033 — the operator attempting the transition is
    /// the same person as another required-distinct role on the change
    /// (requester / tester / signer / approver). Enforces four-eyes++
    /// separation. Mapped to HTTP 409.
    /// </summary>
    public const string ChangeRequestSameOperator = "CHG.SAME_OPERATOR";

    /// <summary>
    /// R2505 / TOR PIR 030-033 — a change request with the same
    /// <see cref="Cnas.Ps.Core.Domain.ChangeRequest.ChangeNumber"/> already
    /// exists (extremely rare; mostly defensive). Mapped to HTTP 409.
    /// </summary>
    public const string ChangeRequestDuplicateNumber = "CHG.DUPLICATE_NUMBER";

    /// <summary>
    /// R2506 / TOR PIR 037-040 — the requested state transition is not legal
    /// for the current <see cref="Cnas.Ps.Core.Domain.QualityRiskStatus"/>.
    /// Mapped to HTTP 409.
    /// </summary>
    public const string QualityRiskInvalidTransition = "QA_RISK.INVALID_TRANSITION";

    /// <summary>
    /// R2506 / TOR PIR 037-040 — a quality risk with the same
    /// <see cref="Cnas.Ps.Core.Domain.QualityRisk.RiskCode"/> already exists.
    /// Mapped to HTTP 409.
    /// </summary>
    public const string QualityRiskDuplicateCode = "QA_RISK.DUPLICATE_CODE";

    /// <summary>
    /// R2506 / TOR PIR 037-040 — the caller is neither the
    /// <see cref="Cnas.Ps.Core.Domain.QualityRisk.OwnerUserId"/> nor a
    /// <c>cnas-admin</c> role-holder, and therefore cannot record a review.
    /// Mapped to HTTP 403.
    /// </summary>
    public const string QualityRiskNotOwner = "QA_RISK.NOT_OWNER";

    /// <summary>
    /// R2506 / TOR PIR 037-040 — the requested preventive-action state
    /// transition is not legal for the current
    /// <see cref="Cnas.Ps.Core.Domain.QualityRiskActionStatus"/>. Mapped to
    /// HTTP 409.
    /// </summary>
    public const string QualityRiskActionInvalidTransition = "QA_RISK.ACTION.INVALID_TRANSITION";

    // --- Classifier reference-blocking (R0402 / TOR CF 17.09) ---

    /// <summary>
    /// R0402 / TOR CF 17.09 — the targeted
    /// <see cref="Cnas.Ps.Core.Domain.Classifier"/> row cannot be
    /// deactivated or deleted because one or more rows in other entities
    /// still hold its <c>(Kind, Code)</c> pair as a foreign reference.
    /// Returned from
    /// <c>Cnas.Ps.Application.UseCases.IClassifierService.DeactivateAsync</c>
    /// after the injected
    /// <c>Cnas.Ps.Application.Classifiers.IClassifierReferenceGuard</c>
    /// reports a non-zero
    /// <c>ClassifierReferenceScanResultDto.ReferencingRowCount</c>.
    /// Mapped to HTTP 409 by the admin controller.
    /// </summary>
    public const string ClassifierReferenced = "CLASSIFIER.REFERENCED";

    /// <summary>
    /// R0401 / TOR CF 17.02-04 — the classifier row carries
    /// <c>IsReadOnlyMirror=true</c> and originates from an official national
    /// register (CAEM Rev.2, CUATM, CFOJ, CFP, NCM). National-mirror rows are
    /// owned by their upstream source and propagated through MConnect; SI PS
    /// keeps them as a local read replica only. Mutating or deactivating such
    /// a row inside SI PS would silently desynchronise the local mirror from
    /// the authoritative national register, so the
    /// <c>Cnas.Ps.Application.UseCases.IClassifierService</c> rejects the
    /// attempt with this stable code instead. Mapped to HTTP 409 by the admin
    /// controller. Returned by both <c>UpsertAsync</c> (when the existing row
    /// is a national mirror) and <c>DeactivateAsync</c>.
    /// </summary>
    public const string ClassifierReadonlyMirror = "CLASSIFIER.READONLY_MIRROR";

    // --- Local login (R0051 / SEC 014) ---

    /// <summary>
    /// R0051 / TOR SEC 014 / CLAUDE.md §5.3 — the supplied local credentials could not
    /// be verified. Used uniformly for unknown login, wrong password, account in any
    /// non-Active state, AND missing <c>UtilizatorAutorizat</c> role so the wire
    /// response never discloses which condition failed (account-enumeration
    /// prevention). The internal audit row carries the specific outcome
    /// (<c>UNKNOWN_LOGIN</c> / <c>BAD_PASSWORD</c> / <c>WRONG_ROLE</c> /
    /// <c>ACCOUNT_LOCKED</c>) for forensic traceability. Mapped to HTTP 400.
    /// </summary>
    public const string LoginInvalid = "LOGIN.INVALID";

    // --- Solicitant deactivation (R0623 / TOR CF 13.04) ---

    /// <summary>
    /// R0623 / TOR CF 13.04 — the targeted
    /// <c>Cnas.Ps.Core.Domain.Solicitant</c> cannot be soft-deactivated
    /// because one or more in-flight (OPEN-state) records still reference it.
    /// Returned from
    /// <c>Cnas.Ps.Application.UseCases.ISolicitantService.DeactivateAsync</c>
    /// after the injected
    /// <c>Cnas.Ps.Application.Solicitants.ISolicitantReferenceGuard</c>
    /// reports a non-zero
    /// <c>SolicitantReferenceScanDto.TotalOpen</c>. The accompanying human
    /// message carries the per-table OPEN counters so the admin UI can render
    /// a precise prompt (e.g. "2 open applications, 1 pending notification").
    /// Mapped to HTTP 409 by the admin controller. Distinct from
    /// <see cref="ClassifierReferenced"/> so dashboards can attribute incidents
    /// to the Solicitant-deactivation surface specifically.
    /// </summary>
    public const string SolicitantReferencedByOpenRecords = "SOLICITANT.REFERENCED_BY_OPEN_RECORDS";

    // --- Examiner assignment (R0570 / TOR CF 08.02) ---

    /// <summary>
    /// R0570 / TOR CF 08.02 — the application could not be submitted because
    /// no eligible examiner remains after the round-robin assignment excludes
    /// the registrar from the candidate pool. Returned from
    /// <c>Cnas.Ps.Application.UseCases.IExaminerAssignmentService.AssignExaminerAsync</c>
    /// and surfaced from
    /// <c>Cnas.Ps.Infrastructure.Services.ApplicationServiceImpl.SubmitAsync</c>
    /// before any row is persisted (CF 08.02 mandates uniform spread + the
    /// registrar-exclusion rule; without a candidate the submission cannot be
    /// routed). Mapped to HTTP 409 by the applications controller. Distinct
    /// from <see cref="NotFound"/> so the admin dashboard can chart "no
    /// examiner capacity" as a load-shaping signal rather than a generic 404.
    /// </summary>
    public const string ApplicationNoAvailableExaminer = "APPLICATION.NO_AVAILABLE_EXAMINER";

    // --- Examiner workflow (R0573 / TOR CF 08.05) ---

    /// <summary>
    /// R0573 / TOR CF 08.05 — the examiner attempted to emit a new decision on
    /// an examination whose parent application has already reached a terminal
    /// status (Approved / Rejected / Closed / Withdrawn). The examination is no
    /// longer editable; the examiner must reopen the dossier through a separate
    /// admin path before emitting further decisions. Returned from
    /// <c>Cnas.Ps.Application.UseCases.IDocumentExaminationService.EmitNewDecisionAsync</c>
    /// and mapped to HTTP 409 by <c>ExaminationController</c>.
    /// </summary>
    public const string ExaminationNotEditable = "EXAMINATION.NOT_EDITABLE";

    /// <summary>
    /// R0573 / TOR CF 08.05 — the supplied decision-template code does not
    /// match any registered Annex 7 <c>IDocxTemplate</c> implementation. The
    /// service refuses to render an unknown template so a typo cannot silently
    /// produce a blank-stub document. Returned from
    /// <c>Cnas.Ps.Application.UseCases.IDocumentExaminationService.EmitNewDecisionAsync</c>
    /// and mapped to HTTP 404 by <c>ExaminationController</c>. Distinct from
    /// <see cref="NotFound"/> so dashboards can attribute incidents to the
    /// template-routing surface specifically.
    /// </summary>
    public const string DocumentTemplateNotFound = "DOCUMENT.TEMPLATE_NOT_FOUND";

    // --- Profile management strategies (R0622 / TOR CF 13.03) ---

    /// <summary>
    /// R0622 / TOR CF 13.03 — the external-sync profile-management strategy
    /// was invoked but the supplied <c>SourceSystem</c> discriminator has no
    /// configured adapter. Returned from
    /// <c>Cnas.Ps.Infrastructure.Services.Profile.ExternalSyncProfileManagementStrategy.ApplyAsync</c>
    /// while the MConnect transport remains externally gated
    /// (EGOV-INTEGRATION-GAP §MConnect). Distinct from <see cref="NotFound"/>
    /// so dashboards can chart "external sync attempted while gated" as a
    /// separate signal from a missing user row. Maps to HTTP 409 / 503 at
    /// the API boundary depending on whether the source system is recognised
    /// but unconfigured (409) versus simply unknown (404).
    /// </summary>
    public const string ProfileExternalSyncNotConfigured = "PROFILE.EXTERNAL_SYNC_NOT_CONFIGURED";

    /// <summary>
    /// R0622 / TOR CF 13.03 — the form-intake profile-management strategy
    /// received a payload that does not parse as JSON or does not declare the
    /// minimum required <c>displayName</c> key. Returned from
    /// <c>Cnas.Ps.Infrastructure.Services.Profile.FormProfileManagementStrategy.ApplyAsync</c>
    /// AFTER the schema-validation pass through <see cref="ValidationFailed"/>
    /// passes — this code signals "intake payload structurally OK but missing
    /// the profile-update keys we need". Maps to HTTP 400 at the API boundary.
    /// </summary>
    public const string ProfileFormIntakePayloadInvalid = "PROFILE.FORM_INTAKE_PAYLOAD_INVALID";

    // --- Granular permissions (R0673 / TOR CF 18.12) ---

    /// <summary>
    /// R0673 / TOR CF 18.12 — the submitted permission verb is not one of the
    /// stable values declared on <see cref="Cnas.Ps.Core.Common.PermissionVerbs"/>.
    /// Returned from
    /// <c>Cnas.Ps.Application.Permissions.IGranularPermissionService.AssignAsync</c>
    /// at submission time so an unknown verb can never reach the DB. Mapped to
    /// HTTP 400 by <c>AdminPermissionsController</c>.
    /// </summary>
    public const string GranularPermissionUnknownVerb = "PERMISSION.UNKNOWN_VERB";

    /// <summary>
    /// R0673 / TOR CF 18.12 — the submitted role code is not one of the stable
    /// values declared on <see cref="Cnas.Ps.Core.Common.RoleCodes"/>. Returned
    /// from
    /// <c>Cnas.Ps.Application.Permissions.IGranularPermissionService.AssignAsync</c>
    /// so a typo in the admin UI cannot silently persist a grant that no caller
    /// will ever match. Mapped to HTTP 400.
    /// </summary>
    public const string GranularPermissionUnknownRole = "PERMISSION.UNKNOWN_ROLE";

    // --- Ad-hoc report builder (R0580 / TOR CF 09.02) ---

    /// <summary>
    /// R0580 / TOR CF 09.02 — the ad-hoc report builder refused the call because
    /// the resolved row set exceeded the hard cap (10 000 rows). Returned from
    /// <c>Cnas.Ps.Application.Reports.IAdHocReportBuilder.BuildAsync</c>; mapped
    /// to HTTP 422 by <c>AdHocReportsController</c> with the row count carried
    /// in the ProblemDetails extension bag so the UI can prompt the caller to
    /// narrow the filter before retrying. Distinct from
    /// <see cref="QueryTooBroad"/> — that fires on registry list services; this
    /// fires on the ad-hoc builder which is gated by a fixed cap rather than a
    /// per-registry budget.
    /// </summary>
    public const string AdHocReportTooLarge = "REPORT.ADHOC_TOO_LARGE";

    /// <summary>
    /// R0580 / TOR CF 09.02 — the supplied <c>EntitySet</c> name is not one of
    /// the wired entities recognised by
    /// <c>Cnas.Ps.Application.Reports.IAdHocReportBuilder</c>. Returned at
    /// submission time so an unknown entity can never reach the LINQ engine.
    /// Mapped to HTTP 400.
    /// </summary>
    public const string AdHocReportUnknownEntity = "REPORT.ADHOC_UNKNOWN_ENTITY";

    /// <summary>
    /// R0580 / TOR CF 09.02 — one or more requested output columns are not
    /// declared on the chosen entity. Returned at submission time. The
    /// accompanying message names the offending column(s). Mapped to HTTP 400.
    /// </summary>
    public const string AdHocReportUnknownColumn = "REPORT.ADHOC_UNKNOWN_COLUMN";

    // --- Paper-channel fulfilment (R0602 / TOR CF 11.03) ---

    /// <summary>
    /// R0602 / TOR CF 11.03 — the requested
    /// <see cref="Cnas.Ps.Core.Domain.PaperFulfilmentStatus"/> transition is not
    /// legal for the row's current state (the state machine only moves forward:
    /// Pending → Printed → Dispatched → Delivered). Returned from
    /// <c>Cnas.Ps.Application.Documents.IPaperFulfilmentService</c> mark-*
    /// methods. Mapped to HTTP 409 by <c>PaperFulfilmentController</c>.
    /// </summary>
    public const string PaperFulfilmentInvalidTransition = "PAPER_FULFILMENT.INVALID_TRANSITION";

    /// <summary>
    /// R0602 / TOR CF 11.03 — a fulfilment row already exists for the supplied
    /// document (the unique <c>DocumentId</c> index trips). Distinct from
    /// <see cref="Conflict"/> so callers can branch on the idempotency guard
    /// specifically. Mapped to HTTP 409.
    /// </summary>
    public const string PaperFulfilmentAlreadyEnqueued = "PAPER_FULFILMENT.ALREADY_ENQUEUED";
}
