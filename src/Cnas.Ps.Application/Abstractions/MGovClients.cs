using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Abstractions;

/// <summary>
/// MSign — government electronic signature service. Used to apply qualified e-signatures
/// to documents emitted by CNAS (decisions, certificates) per TOR §2.5.1.
/// </summary>
/// <remarks>
/// <para>
/// The real MEGA protocol is a <strong>two-phase SOAP / WS-I Basic Profile 1.1</strong>
/// flow over mTLS (no Bearer header — the X.509 client certificate is the identity):
/// </para>
/// <list type="number">
///   <item>
///     <see cref="PostSignRequestAsync"/> — server-to-server SOAP call posting the
///     document (or hash) + return URL. MSign allocates a <c>RequestId</c> and a
///     redirect URL; the CNAS UI then redirects the signer's browser to that URL.
///   </item>
///   <item>
///     The signer completes the ceremony in the MSign portal. When done, MSign POSTs
///     a callback to <c>POST /api/msign/callback/{requestId}</c> on the CNAS side and
///     simultaneously redirects the signer's browser back to the supplied
///     <c>ReturnUrl</c>.
///   </item>
///   <item>
///     <see cref="GetSignResponseAsync"/> — server-to-server SOAP call retrieving the
///     signature bytes + signer metadata. Idempotent — callable any number of times
///     once <see cref="IsRequestReadyAsync"/> returns <c>true</c>.
///   </item>
/// </list>
/// <para>
/// The legacy <see cref="SignAsync"/> entry point is preserved as a back-compat shim
/// (it drives all three steps internally, polling <see cref="IsRequestReadyAsync"/> on
/// a short loop) so existing call sites compile unchanged. New code should call the
/// two-phase API directly so the user-facing browser redirect is correctly threaded
/// through the UI.
/// </para>
/// </remarks>
public interface IMSignClient
{
    /// <summary>
    /// Legacy synchronous signing entry point preserved for back-compat. Internally
    /// drives the full two-phase flow:
    /// <see cref="PostSignRequestAsync"/> → poll <see cref="IsRequestReadyAsync"/> →
    /// <see cref="GetSignResponseAsync"/>.
    /// </summary>
    /// <param name="request">Legacy three-field request shape.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// Deprecated. New code should use the two-phase PostSignRequest/GetSignResponse
    /// flow so the user-facing browser redirect is handled correctly by the UI rather
    /// than being papered over by server-side polling.
    /// </remarks>
    Task<Result<MSignReceipt>> SignAsync(MSignRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Phase 1 of the canonical MSign flow — posts the document (or its hash) to MSign
    /// and receives back a <c>RequestId</c> plus the redirect URL the CNAS UI should
    /// send the signer's browser to. Idempotent on the supplied correlation id.
    /// </summary>
    /// <param name="request">Document, content mode (hash vs. PDF), return URL, and optional correlation id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result{T}.Success(T)"/> wrapping the request id + redirect URL on
    /// success; <see cref="ErrorCodes.MSignFailed"/> when the base URL is unconfigured,
    /// the upstream returns non-2xx, the SOAP envelope cannot be parsed, or a SOAP
    /// fault is returned (the fault string is propagated into the error message).
    /// </returns>
    Task<Result<MSignPostSignResult>> PostSignRequestAsync(
        MSignPostSignRequest request, CancellationToken ct = default);

    /// <summary>
    /// Phase 2 of the canonical MSign flow — retrieves the signature bytes plus signer
    /// metadata once the signer has completed the ceremony. Callable repeatedly; safe
    /// to retry on transport failure.
    /// </summary>
    /// <param name="requestId">The <c>RequestId</c> returned from <see cref="PostSignRequestAsync"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result{T}.Success(T)"/> wrapping the signature bytes + metadata on
    /// success; <see cref="ErrorCodes.MSignFailed"/> on transport / non-2xx / SOAP fault.
    /// </returns>
    Task<Result<MSignGetSignResult>> GetSignResponseAsync(
        string requestId, CancellationToken ct = default);

    /// <summary>
    /// Polls MSign for the readiness of a signing request without consuming the
    /// signature itself. Useful when the caller wants to display a "waiting for the
    /// citizen to sign" status before pulling the signature bytes.
    /// </summary>
    /// <param name="requestId">The <c>RequestId</c> returned from <see cref="PostSignRequestAsync"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result{T}.Success(T)"/> wrapping <c>true</c> if the signature is
    /// available for retrieval, <c>false</c> if MSign is still waiting on the signer;
    /// <see cref="ErrorCodes.MSignFailed"/> on transport / non-2xx / SOAP fault.
    /// </returns>
    Task<Result<bool>> IsRequestReadyAsync(
        string requestId, CancellationToken ct = default);

    /// <summary>Verifies a detached signature against the original payload hash.</summary>
    Task<Result<bool>> VerifyAsync(byte[] payloadHash, byte[] signature, CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies a signed payload's chain of trust against the operator-configured
    /// <see cref="MSignVerifyOptions.TrustedRoots"/>. R0112 / TOR CF 14.06.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is an <em>offline</em> verification — the signed PKCS#7 envelope carries the
    /// signer's leaf certificate (plus, typically, any intermediate certs) so the chain
    /// can be built without an outbound HTTP call to MSign. The verifier:
    /// </para>
    /// <list type="number">
    ///   <item>Parses the input with <c>SignedCms</c> and locates the signer certificate.</item>
    ///   <item>Builds an <c>X509Chain</c> with <see cref="MSignVerifyOptions.TrustedRoots"/>
    ///     as the explicit trust anchor (custom trust store mode — system roots are NOT consulted).</item>
    ///   <item>Validates expiry against the supplied clock.</item>
    ///   <item>Optionally consults CRL/OCSP when
    ///     <see cref="MSignVerifyOptions.RequireRevocationCheck"/> is <c>true</c>; otherwise
    ///     the <see cref="SignatureVerificationResult.RevocationCheckSkipped"/> flag is set and
    ///     <see cref="SignatureVerificationResult.NotRevoked"/> defaults to <c>true</c>.</item>
    /// </list>
    /// <para>
    /// Signature-verification failure is an <em>outcome</em>, not an exception: the method
    /// returns <see cref="Result{T}.Success(T)"/> wrapping a populated
    /// <see cref="SignatureVerificationResult"/> with <c>IsValid=false</c> and the diagnostic
    /// reasons in <see cref="SignatureVerificationResult.ValidationErrors"/>. The
    /// <c>Result.Failure</c> branch is reserved for unrecoverable wiring issues (which the
    /// current implementation has none of).
    /// </para>
    /// </remarks>
    /// <param name="signedPayload">DER-encoded PKCS#7 SignedData envelope. Must not be null.</param>
    /// <param name="options">Trust anchors and verification policy. Must not be null.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result{T}.Success(T)"/> wrapping the verification report. The report's
    /// <see cref="SignatureVerificationResult.IsValid"/> flag is <c>true</c> only when every
    /// gate (chain, expiry, revocation when required) passed.
    /// </returns>
    /// <example>
    /// <code>
    /// var trusted = new[] { LoadRoot("MoldovaQualifiedCA.pem") };
    /// var opts = new MSignVerifyOptions(trusted, RequireRevocationCheck: false, RequireTimestamp: false);
    /// var verify = await msign.VerifySignatureAsync(detachedPkcs7Bytes, opts, ct);
    /// if (verify.Value.IsValid) { /* trusted */ }
    /// </code>
    /// </example>
    Task<Result<SignatureVerificationResult>> VerifySignatureAsync(
        byte[] signedPayload, MSignVerifyOptions options, CancellationToken ct = default);
}

/// <summary>
/// Verification policy passed to
/// <see cref="IMSignClient.VerifySignatureAsync(byte[], MSignVerifyOptions, CancellationToken)"/>.
/// </summary>
/// <param name="TrustedRoots">
/// Explicit list of trust anchors that the chain MUST resolve to. Empty means no chain
/// can be considered trusted — useful for forcing failure during integration testing.
/// The system trust store is NEVER consulted; this prevents accidental acceptance of
/// CAs the operator did not explicitly approve.
/// </param>
/// <param name="RequireRevocationCheck">
/// When <c>true</c>, the verifier performs CRL/OCSP revocation checking. When
/// <c>false</c>, the revocation step is skipped, the report carries
/// <see cref="SignatureVerificationResult.RevocationCheckSkipped"/> = <c>true</c>, and
/// <see cref="SignatureVerificationResult.NotRevoked"/> defaults to <c>true</c>. The
/// CNAS production stance defers this to a follow-up — see R0112 spec.
/// </param>
/// <param name="RequireTimestamp">
/// When <c>true</c>, the verifier additionally asserts the PKCS#7 envelope carries an
/// embedded RFC 3161 timestamp. When <c>false</c>, the timestamp gate is skipped.
/// </param>
public sealed record MSignVerifyOptions(
    IReadOnlyList<System.Security.Cryptography.X509Certificates.X509Certificate2> TrustedRoots,
    bool RequireRevocationCheck,
    bool RequireTimestamp);

/// <summary>
/// Verification report returned by
/// <see cref="IMSignClient.VerifySignatureAsync(byte[], MSignVerifyOptions, CancellationToken)"/>.
/// All fields are populated unconditionally on a non-throwing parse; cert-extracted
/// fields (<see cref="SubjectCn"/>, <see cref="IssuerCn"/>, <see cref="NotBefore"/>,
/// <see cref="NotAfter"/>, <see cref="SerialNumber"/>) carry empty / default values when
/// the input could not be parsed at all (see <see cref="ValidationErrors"/> for the
/// diagnostic in that case).
/// </summary>
/// <param name="IsValid">
/// Overall verdict — <c>true</c> only when every gate passed:
/// <see cref="ChainTrusted"/>, <see cref="NotExpired"/>, and <see cref="NotRevoked"/>.
/// </param>
/// <param name="SubjectCn">Common Name of the signer certificate (empty if unavailable).</param>
/// <param name="IssuerCn">Common Name of the issuing-authority certificate (empty if unavailable).</param>
/// <param name="NotBefore">UTC start of the signer certificate's validity window.</param>
/// <param name="NotAfter">UTC end of the signer certificate's validity window.</param>
/// <param name="SerialNumber">Hex-encoded serial number of the signer certificate.</param>
/// <param name="ChainTrusted">
/// <c>true</c> when the chain successfully resolved to one of
/// <see cref="MSignVerifyOptions.TrustedRoots"/>.
/// </param>
/// <param name="NotExpired">
/// <c>true</c> when the verification instant lies within
/// <see cref="NotBefore"/>..<see cref="NotAfter"/>.
/// </param>
/// <param name="NotRevoked">
/// <c>true</c> when no revocation evidence was found OR when revocation checking was
/// skipped (see <see cref="RevocationCheckSkipped"/>).
/// </param>
/// <param name="RevocationCheckSkipped">
/// <c>true</c> when the operator opted out of CRL/OCSP via
/// <see cref="MSignVerifyOptions.RequireRevocationCheck"/> = <c>false</c>. Operators
/// reading the report should treat <see cref="NotRevoked"/> as "unknown" in that case.
/// </param>
/// <param name="ValidationErrors">
/// Free-form diagnostic strings populated when <see cref="IsValid"/> is <c>false</c>.
/// Empty when the chain validated cleanly.
/// </param>
public sealed record SignatureVerificationResult(
    bool IsValid,
    string SubjectCn,
    string IssuerCn,
    DateTime NotBefore,
    DateTime NotAfter,
    string SerialNumber,
    bool ChainTrusted,
    bool NotExpired,
    bool NotRevoked,
    bool RevocationCheckSkipped,
    IReadOnlyList<string> ValidationErrors);

/// <summary>Inputs for the legacy <see cref="IMSignClient.SignAsync"/> back-compat shim.</summary>
public sealed record MSignRequest(byte[] PayloadHash, string SignerSubject, string Reason);

/// <summary>MSign legacy-shim output — detached signature + protocol metadata.</summary>
public sealed record MSignReceipt(byte[] Signature, string ProtocolReference, DateTimeOffset SignedAt);

/// <summary>
/// Phase-1 request posted to MSign's <c>PostSignRequest</c> SOAP operation. Carries
/// either the full PDF bytes (server-side signing of the entire document) or the
/// pre-computed hash of the document the caller signed locally.
/// </summary>
/// <param name="DocumentBytes">
/// The PDF bytes (<see cref="MSignContentMode.PdfBytes"/>) or the SHA-1/SHA-256 digest
/// (<see cref="MSignContentMode.Hash"/>) — interpretation depends on <paramref name="Mode"/>.
/// </param>
/// <param name="DocumentName">
/// Display name shown to the signer in the MSign portal (e.g. <c>decizia.pdf</c>).
/// </param>
/// <param name="ContentType">
/// MIME type of <paramref name="DocumentBytes"/> (e.g. <c>application/pdf</c>).
/// </param>
/// <param name="Mode">
/// Controls how MSign interprets <paramref name="DocumentBytes"/> — see
/// <see cref="MSignContentMode"/>.
/// </param>
/// <param name="ReturnUrl">
/// The CNAS-side URL MSign redirects the signer's browser to after the ceremony
/// completes. Must be HTTPS in production.
/// </param>
/// <param name="CorrelationId">
/// Optional correlation id forwarded as <c>X-Correlation-Id</c>. When <c>null</c>, the
/// client derives a deterministic id from the canonical request body.
/// </param>
public sealed record MSignPostSignRequest(
    byte[] DocumentBytes,
    string DocumentName,
    string ContentType,
    MSignContentMode Mode,
    Uri ReturnUrl,
    string? CorrelationId);

/// <summary>
/// Controls how MSign interprets the <c>DocumentBytes</c> field of an
/// <see cref="MSignPostSignRequest"/>.
/// </summary>
public enum MSignContentMode
{
    /// <summary>
    /// The bytes are a pre-computed digest (typically SHA-1 or SHA-256). MSign attaches
    /// a detached PKCS#7 signature over the digest and returns the signature bytes.
    /// </summary>
    Hash,

    /// <summary>
    /// The bytes are the complete PDF document. MSign embeds the signature into the
    /// PDF server-side and returns the signed PDF bytes.
    /// </summary>
    PdfBytes,
}

/// <summary>
/// Phase-1 response returned by MSign's <c>PostSignRequest</c> SOAP operation.
/// </summary>
/// <param name="RequestId">
/// MSign-allocated identifier carried through the rest of the flow. Pass to
/// <see cref="IMSignClient.IsRequestReadyAsync"/> and
/// <see cref="IMSignClient.GetSignResponseAsync"/>.
/// </param>
/// <param name="RedirectUrl">
/// The fully-qualified URL the CNAS UI must redirect the signer's browser to. Contains
/// the <see cref="MSignPostSignRequest.ReturnUrl"/> URL-encoded into a query parameter
/// so MSign can route the signer back after the ceremony.
/// </param>
public sealed record MSignPostSignResult(string RequestId, Uri RedirectUrl);

/// <summary>
/// Signer metadata stamped by MSign onto a completed signature. Useful for audit
/// logging and for cross-checking the signer's identity against the original
/// caller's IDNP.
/// </summary>
/// <param name="SignedAtUtc">UTC instant at which MSign issued the signature.</param>
/// <param name="SignerIdnp">IDNP of the human signer who completed the ceremony.</param>
/// <param name="SignerFullName">Display name of the signer; <c>null</c> if MSign chose not to disclose it.</param>
/// <param name="CertificateThumbprint">SHA-1 thumbprint of the signer's certificate; <c>null</c> if unavailable.</param>
public sealed record MSignSignatureMetadata(
    DateTime SignedAtUtc,
    string SignerIdnp,
    string? SignerFullName,
    string? CertificateThumbprint);

/// <summary>Phase-2 response returned by MSign's <c>GetSignResponse</c> SOAP operation.</summary>
/// <param name="SignatureBytes">
/// The raw signature payload. For <see cref="MSignContentMode.Hash"/> requests this is
/// a detached PKCS#7 envelope; for <see cref="MSignContentMode.PdfBytes"/> requests it
/// is the signed PDF.
/// </param>
/// <param name="Metadata">Signer metadata stamped by MSign at the time of signing.</param>
public sealed record MSignGetSignResult(byte[] SignatureBytes, MSignSignatureMetadata Metadata);

/// <summary>
/// MPay — government electronic payments. Used to enqueue outbound social-benefit
/// payments and to accept inbound contribution payments (TOR §2.1, UC14).
/// </summary>
/// <remarks>
/// <para>
/// The real MEGA protocol is a SOAP / WS-Security flow over mTLS (no Bearer header —
/// the X.509 client certificate is the identity, plus an X.509 XML-DSig signature
/// inside the SOAP envelope). The canonical operations are:
/// </para>
/// <list type="number">
///   <item>
///     <see cref="PostOrderAsync"/> — server-to-server SOAP call posting the order
///     descriptor + amount + return URL. MPay allocates an <c>MPayOrderId</c> and a
///     redirect URL; the CNAS UI then redirects the payer's browser to
///     <c>https://mpay.gov.md/service/pay?orderId={mpayOrderId}</c>.
///   </item>
///   <item>
///     The citizen completes payment in the MPay portal. MPay then calls back into
///     CNAS at the inbound REST endpoints
///     <c>GET /api/mpay/orders/{orderId}/details</c> (quote) and
///     <c>POST /api/mpay/orders/{orderId}/confirm</c> (record-payment) and
///     redirects the browser back to the supplied <c>ReturnUrl</c>.
///   </item>
///   <item>
///     <see cref="GetOrderStatusAsync"/> — server-to-server SOAP call reading the
///     current state of an order. Useful for reconciliation jobs that want to confirm
///     a payment that the callback may have missed.
///   </item>
/// </list>
/// <para>
/// The legacy <see cref="SendAsync(MPayOutbound, CancellationToken)"/> entry point is
/// preserved as a back-compat shim — it drives <see cref="PostOrderAsync"/> internally
/// and synthesises an <see cref="MPayReceipt"/> from the posted order so existing call
/// sites (chiefly <c>MPayDispatcherJob</c>) keep compiling. New code should call the
/// three-method API directly so the citizen-facing browser redirect is correctly
/// threaded through the UI.
/// </para>
/// </remarks>
public interface IMPayClient
{
    /// <summary>
    /// Legacy outbound-transfer entry point preserved for back-compat. Internally
    /// drives <see cref="PostOrderAsync"/> using the legacy 4-tuple and synthesises
    /// an <see cref="MPayReceipt"/> echoing the allocated MPay order id as the
    /// upstream transaction reference.
    /// </summary>
    /// <param name="payment">Legacy four-field outbound payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// Deprecated. New code should call <see cref="PostOrderAsync"/> directly and
    /// thread the returned redirect URL through the UI so the citizen sees the MPay
    /// payment ceremony.
    /// </remarks>
    Task<Result<MPayReceipt>> SendAsync(MPayOutbound payment, CancellationToken cancellationToken = default);

    /// <summary>
    /// Posts a new payment order to MPay over SOAP. On success MPay allocates an
    /// <c>MPayOrderId</c> and returns the citizen-facing redirect URL the CNAS UI must
    /// send the payer's browser to. Idempotent on the supplied
    /// <see cref="MPayPostOrderRequest.CorrelationId"/> when present; otherwise the
    /// client derives a deterministic id from the canonical request body.
    /// </summary>
    /// <param name="request">Order descriptor — amount, citizen, service code, return URL, optional correlation id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result{T}.Success(T)"/> wrapping the MPay-allocated order id + redirect URL on success;
    /// <see cref="ErrorCodes.MPayFailed"/> when the base URL is unconfigured, the upstream
    /// returns non-2xx, the SOAP envelope cannot be parsed, or a SOAP fault is returned
    /// (the fault string is propagated into the error message).
    /// </returns>
    Task<Result<MPayPostOrderResult>> PostOrderAsync(MPayPostOrderRequest request, CancellationToken ct = default);

    /// <summary>
    /// Reads the current state of an MPay order (Pending / Confirmed / Cancelled /
    /// Refunded) plus the upstream payment reference and confirmation timestamp once
    /// the payment has settled. Used by reconciliation jobs and by manual recovery
    /// flows when the inbound callback was lost.
    /// </summary>
    /// <param name="orderId">The CNAS-side order identifier originally sent to MPay.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result{T}.Success(T)"/> wrapping the status snapshot on success;
    /// <see cref="ErrorCodes.MPayFailed"/> on transport / non-2xx / SOAP fault / parse failure.
    /// </returns>
    Task<Result<MPayOrderStatus>> GetOrderStatusAsync(string orderId, CancellationToken ct = default);

    /// <summary>
    /// Cancels a still-pending MPay order. Used when the citizen withdraws the
    /// application or when the underlying CNAS decision is reversed before payment.
    /// MPay rejects cancellation of an already-confirmed order with a SOAP fault.
    /// </summary>
    /// <param name="orderId">The CNAS-side order identifier originally sent to MPay.</param>
    /// <param name="reason">Free-text rationale for the cancellation; surfaced in MPay audit logs.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result.Success()"/> when MPay acknowledges the cancellation;
    /// <see cref="ErrorCodes.MPayFailed"/> on transport / non-2xx / SOAP fault.
    /// </returns>
    Task<Result> CancelOrderAsync(string orderId, string reason, CancellationToken ct = default);
}

/// <summary>Inputs for the legacy <see cref="IMPayClient.SendAsync"/> back-compat shim.</summary>
public sealed record MPayOutbound(string BeneficiaryIdnp, string BeneficiaryIban, decimal AmountMdl, string Reference);

/// <summary>MPay legacy-shim output — upstream transaction id + status.</summary>
public sealed record MPayReceipt(string TransactionId, string Status);

/// <summary>
/// Canonical inputs for <see cref="IMPayClient.PostOrderAsync"/> — the SOAP <c>PostOrder</c>
/// operation. Every field maps to an XML element inside the <c>&lt;PostOrder&gt;</c>
/// body element of the outbound envelope.
/// </summary>
/// <param name="OrderId">
/// Stable CNAS-side payment identifier — typically the Sqid-encoded id of the local
/// payment row. Reused across retries so MPay can dedupe.
/// </param>
/// <param name="AmountMdl">Total amount to be collected from the citizen, in Moldovan Lei.</param>
/// <param name="CitizenIdnp">IDNP of the payer (13 digits).</param>
/// <param name="ServiceCode">
/// Service-passport code identifying which CNAS service the payment relates to (e.g.
/// <c>CNAS.PENSION.AGE</c>). Forwarded verbatim to MPay so its reporting can attribute
/// receipts to the originating service.
/// </param>
/// <param name="DescriptionRo">
/// Romanian payment description shown to the citizen on the MPay payment page.
/// </param>
/// <param name="ReturnUrl">
/// The CNAS-side URL MPay redirects the payer's browser to after the payment ceremony
/// completes. Must be HTTPS in production.
/// </param>
/// <param name="CorrelationId">
/// Optional correlation id forwarded as <c>X-Correlation-Id</c>. When <c>null</c>, the
/// client derives a deterministic id from the canonical request body.
/// </param>
public sealed record MPayPostOrderRequest(
    string OrderId,
    decimal AmountMdl,
    string CitizenIdnp,
    string ServiceCode,
    string DescriptionRo,
    Uri ReturnUrl,
    string? CorrelationId);

/// <summary>
/// Outcome of a successful <see cref="IMPayClient.PostOrderAsync"/> call.
/// </summary>
/// <param name="MPayOrderId">
/// Upstream-allocated MPay order identifier. The same value appears in the path of
/// <see cref="RedirectUrl"/> and is the key MPay uses when calling the CNAS-side
/// callback endpoints.
/// </param>
/// <param name="RedirectUrl">
/// Fully-qualified URL the CNAS UI must redirect the payer's browser to. Typically
/// shaped like <c>https://mpay.gov.md/service/pay?orderId={MPayOrderId}</c>.
/// </param>
public sealed record MPayPostOrderResult(string MPayOrderId, Uri RedirectUrl);

/// <summary>
/// Snapshot of an MPay order returned by <see cref="IMPayClient.GetOrderStatusAsync"/>.
/// </summary>
/// <param name="OrderId">CNAS-side order identifier — the same value originally sent to MPay.</param>
/// <param name="State">Current lifecycle state of the order.</param>
/// <param name="AmountMdl">Total order amount, in Moldovan Lei.</param>
/// <param name="PaymentRef">
/// Upstream payment reference once the order is <see cref="MPayOrderState.Confirmed"/>;
/// <c>null</c> while the order is still <see cref="MPayOrderState.Pending"/>.
/// </param>
/// <param name="ConfirmedAtUtc">
/// UTC instant at which MPay recorded the confirmation; <c>null</c> for non-Confirmed states.
/// </param>
public sealed record MPayOrderStatus(
    string OrderId,
    MPayOrderState State,
    decimal AmountMdl,
    string? PaymentRef,
    DateTime? ConfirmedAtUtc);

/// <summary>Lifecycle states of an MPay order.</summary>
public enum MPayOrderState
{
    /// <summary>The citizen has not yet completed the payment ceremony.</summary>
    Pending,

    /// <summary>The citizen has completed payment and MPay has settled the transaction.</summary>
    Confirmed,

    /// <summary>The order was cancelled (by CNAS via <see cref="IMPayClient.CancelOrderAsync"/> or by MPay timeout).</summary>
    Cancelled,

    /// <summary>The confirmed payment was later refunded back to the citizen.</summary>
    Refunded,
}

/// <summary>
/// MConnect — government interoperability platform. Used to call other registries (RSP,
/// RSUD, SFS) and to publish/consume domain events (MConnectEvents). TOR §2.1.
/// </summary>
public interface IMConnectClient
{
    /// <summary>Executes a typed call against an MConnect-exposed service.</summary>
    Task<Result<string>> CallAsync(string serviceCode, string requestJson, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a typed call against an MConnect-exposed service WITH an optional
    /// partner-direct fallback. R0104 / TOR CF 14.03.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When MConnect itself becomes unavailable (timeout, HTTP 5xx, network failure)
    /// AND the partner system has an NDA-gated direct REST endpoint we are allowed to
    /// call, the client invokes <paramref name="fallback"/>'s closure as a last-resort
    /// path. The behaviour is fail-closed — fallback runs ONLY when:
    /// </para>
    /// <list type="bullet">
    ///   <item>The MConnect call failed because of an availability problem (not because
    ///     the partner business logic returned a 4xx — e.g. "person not found" stays a
    ///     partner outcome).</item>
    ///   <item><see cref="MConnectFallback.PartnerHasNda"/> is <c>true</c>
    ///     (configuration-gated per partner).</item>
    ///   <item>The MGov-options <c>AllowFallback</c> flag is enabled (operator opt-in).</item>
    /// </list>
    /// <para>
    /// On fallback invocation, the client increments
    /// <c>cnas.mconnect.fallback_invoked{partner=X}</c> and emits a
    /// <c>MCONNECT.FALLBACK_INVOKED</c> Notice audit row. On fallback failure, it
    /// increments <c>cnas.mconnect.fallback_failed{partner=X}</c> and surfaces a
    /// <c>MCONNECT_FALLBACK_FAILED</c> failure to the caller.
    /// </para>
    /// <para>
    /// When <paramref name="fallback"/> is <c>null</c> the call behaves identically to
    /// <see cref="CallAsync(string, string, CancellationToken)"/>; the typed-facade call
    /// sites do not need to be retrofitted.
    /// </para>
    /// </remarks>
    /// <param name="serviceCode">MConnect service code (e.g. <c>RSP.GetPerson</c>).</param>
    /// <param name="requestJson">Caller-owned request body, opaque to MConnect.</param>
    /// <param name="fallback">
    /// Optional partner-direct fallback descriptor. When <c>null</c> the call is identical
    /// to the unparameterised overload.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result{T}.Success(T)"/> wrapping the partner's JSON response on success
    /// (whether the response came from MConnect OR the fallback);
    /// <see cref="Cnas.Ps.Core.Common.ErrorCodes.MConnectFailed"/> when MConnect failed and
    /// fallback was not eligible; a result whose error code is
    /// <c>MCONNECT_FALLBACK_FAILED</c> when MConnect AND the fallback both failed.
    /// </returns>
    Task<Result<string>> CallAsync(
        string serviceCode,
        string requestJson,
        MConnectFallback? fallback,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Descriptor for the partner-direct fallback path consulted by
/// <see cref="IMConnectClient.CallAsync(string, string, MConnectFallback?, CancellationToken)"/>
/// when MConnect itself is unavailable. R0104 / TOR CF 14.03.
/// </summary>
/// <param name="PartnerSystemCode">
/// Stable partner code (e.g. <c>RSP</c>, <c>SFS</c>) — tagged on the audit row and the
/// fallback metrics so operators can chart per-partner fallback volume without unbounded
/// cardinality.
/// </param>
/// <param name="DirectInvoke">
/// The partner-direct REST closure to invoke when MConnect is unavailable. Must return
/// a <see cref="Result{T}"/> shaped identically to a normal MConnect response (raw JSON
/// string the typed facade can deserialise). The closure owns its own HTTP / retry
/// policy.
/// </param>
/// <param name="PartnerHasNda">
/// Configuration gate — <c>true</c> only when CNAS holds an NDA with the partner system
/// authorising us to bypass MConnect. When <c>false</c>, the MConnect failure surfaces
/// unchanged and the closure is NOT invoked.
/// </param>
public sealed record MConnectFallback(
    string PartnerSystemCode,
    Func<CancellationToken, Task<Result<string>>> DirectInvoke,
    bool PartnerHasNda);

/// <summary>
/// MNotify — citizen notification dispatch (email, SMS). UC22. Real MEGA endpoint is
/// <c>POST /api/Notification</c> with the multi-language <see cref="NotificationRequest"/>
/// body shape. The legacy <see cref="SendAsync(MNotifyMessage, CancellationToken)"/>
/// signature is kept as a back-compat shim that translates the old tuple into a
/// <see cref="NotificationRequest"/> so existing call sites compile unchanged.
/// </summary>
public interface IMNotifyClient
{
    /// <summary>
    /// Dispatches a multi-language notification through MNotify
    /// (<c>POST /api/Notification</c>). On 2xx returns the upstream-assigned notification id.
    /// </summary>
    /// <param name="request">The notification payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result{T}.Success(T)"/> wrapping the notification id on success;
    /// <see cref="ErrorCodes.ValidationFailed"/> when the request has no recipients;
    /// <see cref="ErrorCodes.MNotifyFailed"/> when the base URL is not configured, the
    /// upstream returns non-2xx, or transport fails.
    /// </returns>
    Task<Result<string>> SendNotificationAsync(NotificationRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Legacy back-compat dispatch entry point. Translates the supplied 4-tuple into a
    /// <see cref="NotificationRequest"/> and forwards to
    /// <see cref="SendNotificationAsync(NotificationRequest, CancellationToken)"/>. New code
    /// should call <see cref="SendNotificationAsync(NotificationRequest, CancellationToken)"/>
    /// directly so it can populate multi-language subjects and typed recipients.
    /// </summary>
    /// <param name="message">Legacy 4-tuple payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<Result> SendAsync(MNotifyMessage message, CancellationToken cancellationToken = default);
}

/// <summary>Inputs for the legacy MNotify dispatch shim.</summary>
public sealed record MNotifyMessage(string RecipientIdnp, string Channel, string TemplateCode, IReadOnlyDictionary<string, string> Parameters);

/// <summary>
/// Canonical multi-language MNotify request posted to <c>POST /api/Notification</c>. Every
/// language entry in <see cref="Subject"/>, <see cref="Body"/>, and <see cref="BodyShort"/>
/// is keyed by an ISO 639-1 language code (e.g. <c>ro</c>, <c>ru</c>).
/// </summary>
/// <param name="Subject">Localised subject lines (one entry per language tag).</param>
/// <param name="Body">Localised body text (one entry per language tag).</param>
/// <param name="BodyShort">Optional short-form body (e.g. for SMS). Null when not supplied.</param>
/// <param name="Recipients">Typed recipients — at least one is required; otherwise <see cref="ErrorCodes.ValidationFailed"/>.</param>
/// <param name="Attachments">Optional base64-encoded attachments. Null when not supplied.</param>
/// <param name="CorrelationId">Optional X-Correlation-Id header value; null falls back to a deterministic derivation.</param>
public sealed record NotificationRequest(
    IReadOnlyDictionary<string, string> Subject,
    IReadOnlyDictionary<string, string> Body,
    IReadOnlyDictionary<string, string>? BodyShort,
    IReadOnlyList<NotificationRecipient> Recipients,
    IReadOnlyList<NotificationAttachment>? Attachments,
    string? CorrelationId);

/// <summary>Typed recipient — Type controls how Value is interpreted.</summary>
/// <param name="Type">Recipient type — see <see cref="NotificationRecipientType"/>.</param>
/// <param name="Value">Email address, IDNP, or MSISDN matching <paramref name="Type"/>.</param>
public sealed record NotificationRecipient(NotificationRecipientType Type, string Value);

/// <summary>
/// Recipient identifier kind. The wire spelling is mixed-case per MEGA spec —
/// <c>email</c> / <c>IDNP</c> / <c>msisdn</c> — handled by a custom JSON converter
/// inside <c>MNotifyClient</c>.
/// </summary>
public enum NotificationRecipientType
{
    /// <summary>Email address — wire value <c>"email"</c>.</summary>
    Email,

    /// <summary>Moldovan national identification number (13 digits) — wire value <c>"IDNP"</c>.</summary>
    Idnp,

    /// <summary>Mobile subscriber number in E.164 (e.g. <c>+37368...</c>) — wire value <c>"msisdn"</c>.</summary>
    Msisdn,
}

/// <summary>Base64-encoded attachment included alongside a notification.</summary>
/// <param name="FileName">Display file name (e.g. <c>decision.pdf</c>).</param>
/// <param name="ContentBase64">Base64-encoded payload.</param>
/// <param name="ContentType">MIME type (e.g. <c>application/pdf</c>).</param>
public sealed record NotificationAttachment(string FileName, string ContentBase64, string ContentType);

/// <summary>
/// MLog — central government journaling service. Critical business events are mirrored
/// here in parallel with the local audit log per SEC 056. The canonical wire shape is
/// the 16-field <see cref="MLogEvent"/> record posted to <c>POST /register</c>; the
/// legacy <see cref="AppendAsync(MLogEntry, CancellationToken)"/> entry point is kept as
/// a thin shim that translates the five-field <see cref="MLogEntry"/> into a canonical
/// event so existing call sites continue to compile while we migrate callers (see
/// <c>docs/EGOV-INTEGRATION-GAP.md</c> §"MLog").
/// </summary>
public interface IMLogClient
{
    /// <summary>
    /// Legacy entry point preserved for back-compat. Translates the 5-field
    /// <see cref="MLogEntry"/> into an <see cref="MLogEvent"/> and forwards to
    /// <see cref="RegisterEventAsync(MLogEvent, CancellationToken)"/>. New code should
    /// build an <see cref="MLogEvent"/> directly so it can populate the full canonical
    /// shape (legal basis, correlation id, user session, ...).
    /// </summary>
    /// <param name="entry">Legacy five-field event payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result.Success"/> on a 2xx upstream response;
    /// <see cref="ErrorCodes.MLogFailed"/> on transport / non-2xx / malformed JSON.
    /// </returns>
    Task<Result> AppendAsync(MLogEntry entry, CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers a canonical 16-field event with MLog (<c>POST /register</c>). The
    /// upstream service deduplicates by <see cref="MLogEvent.EventId"/> so re-posting an
    /// event with the same id is a no-op (idempotency).
    /// </summary>
    /// <param name="evt">The event to register.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result{T}.Success(T)"/> wrapping the upstream-assigned uid on success;
    /// <see cref="ErrorCodes.MLogFailed"/> on transport / non-2xx / malformed JSON;
    /// <see cref="ErrorCodes.MLogFailed"/> when the base URL is not configured (local
    /// dev safety — no HTTP call is attempted in that case).
    /// </returns>
    Task<Result<string>> RegisterEventAsync(MLogEvent evt, CancellationToken ct = default);

    /// <summary>
    /// Searches the MLog journal using the upstream filter expression
    /// (<c>GET /query</c>). The exact filter grammar is owned by MLog — CNAS forwards
    /// the string verbatim so operators can paste queries from the MLog admin console
    /// without translation.
    /// </summary>
    /// <param name="filter">Upstream filter expression (e.g. <c>"event_type='Cnas.Application.Submitted'"</c>). Forwarded verbatim.</param>
    /// <param name="skip">Number of matches to skip (paging).</param>
    /// <param name="take">Page size; bounded by the upstream maximum.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result{T}.Success(T)"/> wrapping the matching page on success;
    /// <see cref="ErrorCodes.MLogFailed"/> on upstream / transport failure.
    /// </returns>
    Task<Result<MLogQueryResult>> QueryAsync(string filter, int skip = 0, int take = 50, CancellationToken ct = default);

    /// <summary>
    /// Reads a single registered event by its upstream uid (<c>GET /query/{uid}</c>).
    /// The uid is the value returned from
    /// <see cref="RegisterEventAsync(MLogEvent, CancellationToken)"/> at registration time.
    /// </summary>
    /// <param name="uid">Upstream-assigned event uid (returned from <see cref="RegisterEventAsync(MLogEvent, CancellationToken)"/>).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result{T}.Success(T)"/> wrapping the event on success;
    /// <see cref="ErrorCodes.MLogFailed"/> on upstream / transport failure.
    /// </returns>
    Task<Result<MLogEvent>> GetByUidAsync(string uid, CancellationToken ct = default);
}

/// <summary>Inputs for the legacy MLog append shim. See <see cref="IMLogClient.AppendAsync(MLogEntry, CancellationToken)"/>.</summary>
public sealed record MLogEntry(string EventCode, string ActorId, string? TargetEntity, long? TargetEntityId, string DetailsJson);

/// <summary>
/// Canonical 16-field MLog event posted to <c>POST /register</c>. Mirrors the wire
/// contract published by AGE — every field maps 1:1 to a snake_case JSON key
/// (<c>event_time</c>, <c>event_type</c>, ...). See
/// <c>docs/EGOV-INTEGRATION-GAP.md</c> §"MLog" for the upstream specification.
/// </summary>
/// <param name="EventTime">UTC instant at which the business event occurred. Serialised as ISO-8601.</param>
/// <param name="EventType">Dotted event hierarchy in the producer's namespace (e.g. <c>Cnas.Application.Submitted</c>).</param>
/// <param name="EventId">Stable producer-side id (typically a GUID). Reused across retries so MLog can deduplicate.</param>
/// <param name="EventCorrelation">Cross-system correlation id linking the event to the originating user request / job.</param>
/// <param name="EventLevel">Severity classification. Serialised as a string (<c>Info</c>, <c>Warning</c>, <c>Error</c>, <c>Critical</c>).</param>
/// <param name="EventSource">Identifier of the producer subsystem (always <c>CNAS-PS</c> for events emitted by this service).</param>
/// <param name="EventMessage">Short human-readable description (one line). PII-free.</param>
/// <param name="EventDetails">Optional opaque payload, serialised as JSON. Owner-defined schema; PII allowed.</param>
/// <param name="LegalEntity">Optional legal entity on whose behalf the event was emitted (e.g. <c>CNAS</c>).</param>
/// <param name="LegalBasis">Optional citation of the law / regulation authorising the processing (e.g. <c>Lege 156/1998</c>).</param>
/// <param name="LegalReason">Optional free-text rationale supplementing <paramref name="LegalBasis"/>.</param>
/// <param name="User">Optional IDNP or username of the human / system actor that triggered the event.</param>
/// <param name="UserSession">Optional MPass session id linking the event to a specific browser session.</param>
/// <param name="UserAddress">Optional IP address or device fingerprint of the actor.</param>
/// <param name="Subject">Optional IDNP of the data subject the event concerns (may differ from <paramref name="User"/>).</param>
/// <param name="Object">Optional resource path or id describing the entity acted upon (e.g. <c>Application/42</c>). Named to match the upstream <c>object</c> wire field.</param>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "The 'object' parameter name is mandated by the MEGA MLog wire spec; the snake_case JSON field is 'object'. Renaming would break compatibility.")]
public sealed record MLogEvent(
    DateTime EventTime,
    string EventType,
    string EventId,
    string? EventCorrelation,
    MLogEventLevel EventLevel,
    string EventSource,
    string EventMessage,
    string? EventDetails,
    string? LegalEntity,
    string? LegalBasis,
    string? LegalReason,
    string? User,
    string? UserSession,
    string? UserAddress,
    string? Subject,
    string? Object);

/// <summary>
/// Severity classification carried on every <see cref="MLogEvent"/>. Serialised as a
/// string on the wire so the upstream service stays readable without an enum lookup.
/// </summary>
public enum MLogEventLevel
{
    /// <summary>Informational — routine business event (default).</summary>
    Info,

    /// <summary>Degraded but non-blocking condition. Operators should review at convenience.</summary>
    Warning,

    /// <summary>Failure of a single operation. Operators should investigate promptly.</summary>
    Error,

    /// <summary>Systemic failure or security-sensitive event. Page on-call.</summary>
    Critical,
}

/// <summary>
/// Paged result from <see cref="IMLogClient.QueryAsync(string, int, int, CancellationToken)"/>.
/// </summary>
/// <param name="Items">The events on the current page, in the order MLog returned them.</param>
/// <param name="TotalCount">Total matching events upstream (independent of the page size).</param>
public sealed record MLogQueryResult(IReadOnlyList<MLogEvent> Items, int TotalCount);

// NOTE: IMPowerClient / MPowerVerifyRequest / MPowerVerification removed. MPower is
// consumed indirectly via MPass SAML claims — the SAML assertion carries an
// "OnBehalfOfPrincipalIdnp" claim populated only if the citizen authorised the operator
// through the MPower portal. The application service compares this claim to the
// requested principal and either proceeds or returns ErrorCodes.MPowerNotAuthorized.
// See docs/EGOV-INTEGRATION-GAP.md §"MPower" for full design notes.

/// <summary>
/// MConnect Events — real-time event streaming over CloudEvents v1.0. Producers publish
/// domain events; subscribers receive them via WebSocket (recommended) or long-polling
/// (fallback). See <c>docs/EGOV-INTEGRATION-GAP.md</c> for the upstream contract.
/// </summary>
/// <remarks>
/// The producer is idempotent on <see cref="CloudEventEnvelope.Id"/>: republishing the
/// same envelope with the same id is a no-op upstream. CNAS callers must therefore
/// generate stable ids (e.g. SHA-256 of the business key) for retry safety.
/// </remarks>
public interface IMConnectEventsProducer
{
    /// <summary>
    /// Publishes a single CloudEvent. Idempotent on the event id — re-sending the same
    /// envelope is safe and will not produce a duplicate downstream.
    /// </summary>
    /// <param name="envelope">The CloudEvent to publish (id, source, type, data, ...).</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// <see cref="Result.Success"/> on a 2xx upstream response, or
    /// <see cref="ErrorCodes.MConnectFailed"/> on transport / non-2xx /
    /// JSON failure. Returns <see cref="ErrorCodes.Internal"/> if the
    /// MConnect Events base URL is not configured.
    /// </returns>
    Task<Result> PublishAsync(CloudEventEnvelope envelope, CancellationToken ct = default);

    /// <summary>
    /// Publishes a batch of CloudEvents in a single HTTP round-trip. Recommended over
    /// repeated <see cref="PublishAsync(CloudEventEnvelope, CancellationToken)"/> calls
    /// for throughput-sensitive scenarios (overnight aggregations, bulk decision events).
    /// </summary>
    /// <param name="envelopes">The CloudEvents to publish, in order.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// <see cref="Result.Success"/> on a 2xx upstream response, or
    /// <see cref="ErrorCodes.MConnectFailed"/> on transport / non-2xx /
    /// JSON failure. Returns <see cref="ErrorCodes.Internal"/> if the
    /// MConnect Events base URL is not configured.
    /// </returns>
    Task<Result> PublishBatchAsync(IReadOnlyList<CloudEventEnvelope> envelopes, CancellationToken ct = default);
}

/// <summary>
/// CloudEvents v1.0 envelope as posted to / received from MConnect Events. The
/// <c>data</c> payload is a raw JSON string so each producer/consumer owns the schema
/// for its own event type (mirrors the MConnect-routed-service convention in
/// <see cref="IMConnectClient"/>).
/// </summary>
/// <param name="Id">Unique event id. Reused across retries to make republishing idempotent.</param>
/// <param name="Source">CloudEvents <c>source</c> (URI identifying the producer system, e.g. <c>cnas-ps</c>).</param>
/// <param name="Type">CloudEvents <c>type</c> (reverse-DNS event name, e.g. <c>md.cnas.ps.decision.issued.v1</c>).</param>
/// <param name="TimeUtc">CloudEvents <c>time</c> in UTC. Serialised as ISO-8601 "O" format.</param>
/// <param name="PartitionKey">Optional partition key for ordered consumption (CloudEvents extension).</param>
/// <param name="DataContentType">Media type of the <see cref="DataJson"/> payload (typically <c>application/json</c>).</param>
/// <param name="DataJson">Raw event payload, serialised as JSON. Passed through verbatim — caller owns the schema.</param>
public sealed record CloudEventEnvelope(
    string Id,
    string Source,
    string Type,
    DateTime TimeUtc,
    string? PartitionKey,
    string DataContentType,
    string DataJson)
{
    /// <summary>CloudEvents specification version this envelope conforms to. Always <c>1.0</c>.</summary>
    public string SpecVersion => "1.0";
}

/// <summary>
/// Strategy for dispatching a received CloudEvent to domain handlers. The MConnect Events
/// consumer resolves handlers from DI on each receipt and routes by <see cref="CanHandle"/>.
/// </summary>
public interface ICloudEventHandler
{
    /// <summary>
    /// Returns <c>true</c> if this handler accepts the given <paramref name="eventType"/>.
    /// Multiple handlers may accept the same type; all matching handlers are invoked.
    /// </summary>
    /// <param name="eventType">CloudEvents <c>type</c> attribute of the received event.</param>
    bool CanHandle(string eventType);

    /// <summary>Processes the received event. Implementations must be tolerant of replays.</summary>
    /// <param name="envelope">The received CloudEvent.</param>
    /// <param name="ct">Cancellation requested when the consumer is stopping.</param>
    Task HandleAsync(CloudEventEnvelope envelope, CancellationToken ct);
}

/// <summary>
/// MDocs — government managed-document storage with version history and signing
/// co-ordination. Used to externalise authoritative copies of CNAS-emitted documents
/// (Decizia, certificates, signed declarations) into the whole-of-government document
/// vault so other agencies and the citizen-facing portal can pull them by stable id.
/// See <c>docs/EGOV-INTEGRATION-GAP.md</c> §"MDocs" for the protocol details.
/// </summary>
/// <remarks>
/// <para>
/// The current iteration speaks plain bearer-token HTTP against a JSON+multipart REST
/// surface; the production contract will require client-certificate authentication plus
/// an MPass-issued bearer token (mTLS + token-forwarding pattern shared with MConnect
/// Events). The HTTP shape stays the same — only the transport hardens — so callers can
/// integrate against this interface today and the implementation will swap out the
/// auth chain when the MEGA certificate distribution is in place.
/// </para>
/// <para>
/// All identifiers returned (<see cref="MDocsUploadReceipt.DocumentId"/>,
/// <see cref="MDocsMetadata.DocumentId"/>) are opaque strings allocated by MDocs and
/// are NOT Sqid-encoded: they are an external system's identifier, not ours.
/// </para>
/// </remarks>
public interface IMDocsClient
{
    /// <summary>
    /// Uploads a single document to MDocs. Returns the upstream-allocated identifier,
    /// version tag, and content hash so the caller can persist the binding locally.
    /// </summary>
    /// <param name="request">Document contents and metadata.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result{T}.Success(T)"/> wrapping the receipt on success;
    /// <see cref="Cnas.Ps.Core.Common.ErrorCodes.MDocsFailed"/> on transport / upstream
    /// failure; <see cref="Cnas.Ps.Core.Common.ErrorCodes.Internal"/> when the client is
    /// not configured (empty base URL — local-dev safety).
    /// </returns>
    Task<Result<MDocsUploadReceipt>> UploadAsync(MDocsUploadRequest request, CancellationToken ct = default);

    /// <summary>
    /// Downloads the raw bytes of the document identified by <paramref name="documentId"/>.
    /// The caller owns the returned <see cref="Stream"/> and must dispose it.
    /// </summary>
    /// <param name="documentId">MDocs-allocated document identifier (opaque string).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Result<Stream>> DownloadAsync(string documentId, CancellationToken ct = default);

    /// <summary>
    /// Reads the metadata block for the document identified by <paramref name="documentId"/>
    /// (filename, content type, size, hash, version, upload timestamp, tag dictionary).
    /// </summary>
    /// <param name="documentId">MDocs-allocated document identifier (opaque string).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Result<MDocsMetadata>> GetMetadataAsync(string documentId, CancellationToken ct = default);
}

/// <summary>Inputs for an MDocs upload.</summary>
/// <param name="FileName">Human-readable filename (e.g. <c>Decizia_D-2026-ABCD1234.pdf</c>).</param>
/// <param name="ContentType">MIME type of <paramref name="Content"/>.</param>
/// <param name="Content">Raw document bytes; must not be null and must be non-empty.</param>
/// <param name="CategoryCode">Optional MDocs category code (e.g. <c>CNAS.DECISION</c>).</param>
/// <param name="Tags">
/// Optional, opaque key/value tag dictionary forwarded verbatim to MDocs. Useful for
/// linking the upload back to a dossier or application via stable Sqid references.
/// </param>
public sealed record MDocsUploadRequest(
    string FileName,
    string ContentType,
    byte[] Content,
    string? CategoryCode,
    IReadOnlyDictionary<string, string>? Tags);

/// <summary>MDocs upload outcome — upstream identifier + integrity metadata.</summary>
/// <param name="DocumentId">Opaque MDocs-allocated document id; stable across versions.</param>
/// <param name="Version">Version tag of the just-uploaded revision (e.g. <c>v1</c>).</param>
/// <param name="Sha256">SHA-256 of the uploaded bytes, hex-encoded; lets the caller verify storage integrity.</param>
/// <param name="UploadedAtUtc">UTC timestamp stamped by MDocs at receipt.</param>
public sealed record MDocsUploadReceipt(
    string DocumentId,
    string Version,
    string Sha256,
    DateTime UploadedAtUtc);

/// <summary>MDocs metadata block returned by <see cref="IMDocsClient.GetMetadataAsync"/>.</summary>
/// <param name="DocumentId">Opaque MDocs-allocated document id.</param>
/// <param name="FileName">Original filename submitted at upload time.</param>
/// <param name="ContentType">MIME type of the stored bytes.</param>
/// <param name="SizeBytes">Size of the stored bytes.</param>
/// <param name="Sha256">SHA-256 of the stored bytes, hex-encoded.</param>
/// <param name="Version">Current version tag (e.g. <c>v3</c>).</param>
/// <param name="UploadedAtUtc">UTC timestamp at which the current version was stored.</param>
/// <param name="Tags">Free-form tag dictionary supplied at upload time.</param>
public sealed record MDocsMetadata(
    string DocumentId,
    string FileName,
    string ContentType,
    long SizeBytes,
    string Sha256,
    string Version,
    DateTime UploadedAtUtc,
    IReadOnlyDictionary<string, string> Tags);
