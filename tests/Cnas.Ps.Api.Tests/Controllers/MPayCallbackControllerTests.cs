using System;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Api.Security;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// Direct-construction unit tests for <see cref="MPayCallbackController"/>. MPay calls
/// these endpoints server-to-server: <c>GET /api/mpay/orders/{orderId}/details</c> to
/// quote the unpaid amount + descriptor before initiating the citizen-facing payment
/// ceremony, and <c>POST /api/mpay/orders/{orderId}/confirm</c> to record the
/// confirmed payment. Both are exposed anonymously — the trust boundary is the mTLS
/// handshake at the gateway, not an Authorization header. The confirm endpoint MUST be
/// idempotent (CLAUDE.md cross-cutting "Idempotent Callbacks") — a retried POST with
/// the same payload returns 200 OK and produces no duplicate side effect; a retried POST
/// with a DIFFERENT payment reference returns 409 Conflict (never silently overwrites).
/// </summary>
public sealed class MPayCallbackControllerTests
{
    /// <summary>UTC instant used by every test that needs a confirmation timestamp.</summary>
    private static readonly DateTime ConfirmedAt = new(2026, 5, 20, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>Builds an SUT wired to the supplied store substitute.</summary>
    private static ICallbackSignatureVerifier AllowingVerifier()
    {
        var verifier = Substitute.For<ICallbackSignatureVerifier>();
        verifier.Verify(
                CallbackSignatureProvider.MPay,
                Arg.Any<string>(),
                Arg.Any<IHeaderDictionary>())
            .Returns(CallbackSignatureVerificationResult.Success());
        return verifier;
    }

    private static ICallbackSignatureVerifier RejectingVerifier()
    {
        var verifier = Substitute.For<ICallbackSignatureVerifier>();
        verifier.Verify(
                CallbackSignatureProvider.MPay,
                Arg.Any<string>(),
                Arg.Any<IHeaderDictionary>())
            .Returns(CallbackSignatureVerificationResult.Failure("signature missing"));
        return verifier;
    }

    /// <summary>Builds an SUT wired to the supplied store substitute.</summary>
    private static MPayCallbackController BuildSut(
        IMPayOrderStore store,
        ICallbackSignatureVerifier? verifier = null)
    {
        var sut = new MPayCallbackController(
            NullLogger<MPayCallbackController>.Instance,
            store,
            verifier ?? AllowingVerifier());
        sut.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext(),
        };
        return sut;
    }

    // ─────────────────────── GET /details ───────────────────────

    /// <summary>
    /// Read miss: an unknown order id must return 404. The previous v1 stub returned a
    /// zero-amount placeholder; the A2 batch tightens this to surface a real "not found"
    /// so the upstream MPay payment page can fail loudly when CNAS has no matching order.
    /// </summary>
    [Fact]
    public async Task Details_UnknownOrderId_Returns404()
    {
        var store = Substitute.For<IMPayOrderStore>();
        store.GetByOrderIdAsync("ORD-UNKNOWN", Arg.Any<CancellationToken>())
            .Returns((MPayOrderSnapshot?)null);
        var sut = BuildSut(store);

        var result = await sut.GetOrderDetailsAsync("ORD-UNKNOWN", default);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Details_MissingValidSignature_Returns401WithoutReadingStore()
    {
        var store = Substitute.For<IMPayOrderStore>();
        var sut = BuildSut(store, RejectingVerifier());

        var result = await sut.GetOrderDetailsAsync("ORD-UNSIGNED", default);

        result.Should().BeOfType<UnauthorizedObjectResult>();
        await store.DidNotReceiveWithAnyArgs().GetByOrderIdAsync(default!, default);
    }

    /// <summary>
    /// Read hit: a known order id is projected into the canonical four-field response
    /// body verbatim — the field shape is the wire contract owned by MPay.
    /// </summary>
    [Fact]
    public async Task Details_KnownOrderId_Returns200WithCanonicalBody()
    {
        var store = Substitute.For<IMPayOrderStore>();
        store.GetByOrderIdAsync("ORD-1", Arg.Any<CancellationToken>())
            .Returns(new MPayOrderSnapshot(
                OrderId: "ORD-1",
                AmountMdl: 250.75m,
                DescriptionRo: "Plata contributii CNAS",
                BeneficiaryIdnp: "2000000000007",
                PaymentRef: null,
                ConfirmedAtUtc: null));
        var sut = BuildSut(store);

        var result = await sut.GetOrderDetailsAsync("ORD-1", default);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.StatusCode.Should().Be(200);
        var body = ok.Value.Should().BeOfType<MPayOrderDetailsResponse>().Subject;
        body.OrderId.Should().Be("ORD-1");
        body.AmountMdl.Should().Be(250.75m);
        body.DescriptionRo.Should().Be("Plata contributii CNAS");
        body.BeneficiaryIdnp.Should().Be("2000000000007");
    }

    /// <summary>
    /// An empty <c>orderId</c> route segment must produce a deterministic 400 — the
    /// route binder cannot reach a real persistence call with no key.
    /// </summary>
    [Fact]
    public async Task Details_EmptyOrderId_Returns400()
    {
        var store = Substitute.For<IMPayOrderStore>();
        var sut = BuildSut(store);

        var result = await sut.GetOrderDetailsAsync(string.Empty, default);

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(400);
    }

    // ─────────────────────── POST /confirm ───────────────────────

    /// <summary>
    /// First-time confirmation: the store records the payment reference + timestamp and
    /// returns success. The controller maps this to 200 OK.
    /// </summary>
    [Fact]
    public async Task Confirm_FirstTime_Returns200AndCallsStore()
    {
        var store = Substitute.For<IMPayOrderStore>();
        store.ConfirmAsync("ORD-2", "BANK-REF-1", ConfirmedAt, Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        var sut = BuildSut(store);

        var result = await sut.ConfirmOrderPaymentAsync(
            "ORD-2",
            new MPayConfirmRequest("BANK-REF-1", ConfirmedAt),
            default);

        result.Should().BeOfType<OkResult>();
        await store.Received(1).ConfirmAsync(
            "ORD-2",
            "BANK-REF-1",
            ConfirmedAt,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Confirm_MissingValidSignature_Returns401WithoutMutatingStore()
    {
        var store = Substitute.For<IMPayOrderStore>();
        var sut = BuildSut(store, RejectingVerifier());

        var result = await sut.ConfirmOrderPaymentAsync(
            "ORD-UNSIGNED",
            new MPayConfirmRequest("BANK-REF-1", ConfirmedAt),
            default);

        result.Should().BeOfType<UnauthorizedObjectResult>();
        await store.DidNotReceiveWithAnyArgs().ConfirmAsync(default!, default!, default, default);
    }

    /// <summary>
    /// Idempotent replay: a retried POST with the same payment reference must return
    /// 200 OK both times. Modelled by the store returning success twice (the second call
    /// is a no-op write inside the store; the controller has no visibility into that —
    /// only the success result). CLAUDE.md cross-cutting "Idempotent Callbacks".
    /// </summary>
    [Fact]
    public async Task Confirm_SamePaymentRefTwice_Returns200OnBoth()
    {
        var store = Substitute.For<IMPayOrderStore>();
        store.ConfirmAsync("ORD-3", "BANK-REF-IDEMPO", ConfirmedAt, Arg.Any<CancellationToken>())
            .Returns(Result.Success(), Result.Success());
        var sut = BuildSut(store);

        var first = await sut.ConfirmOrderPaymentAsync(
            "ORD-3",
            new MPayConfirmRequest("BANK-REF-IDEMPO", ConfirmedAt),
            default);
        var second = await sut.ConfirmOrderPaymentAsync(
            "ORD-3",
            new MPayConfirmRequest("BANK-REF-IDEMPO", ConfirmedAt),
            default);

        first.Should().BeOfType<OkResult>();
        second.Should().BeOfType<OkResult>();
    }

    /// <summary>
    /// Conflicting replay: a retried POST with a DIFFERENT payment reference on an
    /// already-confirmed order must return 409 Conflict — the store will not silently
    /// overwrite the original confirmation.
    /// </summary>
    [Fact]
    public async Task Confirm_DifferentPaymentRefOnConfirmedOrder_Returns409()
    {
        var store = Substitute.For<IMPayOrderStore>();
        store.ConfirmAsync("ORD-4", "BANK-REF-OTHER", ConfirmedAt, Arg.Any<CancellationToken>())
            .Returns(Result.Failure(ErrorCodes.Conflict, "Order is already confirmed."));
        var sut = BuildSut(store);

        var result = await sut.ConfirmOrderPaymentAsync(
            "ORD-4",
            new MPayConfirmRequest("BANK-REF-OTHER", ConfirmedAt),
            default);

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(409);
    }

    /// <summary>
    /// Confirming an unknown order id returns 404 — distinct from 409 so MPay (and the
    /// operations dashboards reading the structured logs) can tell "we never originated
    /// this order" apart from "the order exists but has been confirmed differently".
    /// </summary>
    [Fact]
    public async Task Confirm_UnknownOrderId_Returns404()
    {
        var store = Substitute.For<IMPayOrderStore>();
        store.ConfirmAsync("ORD-NOPE", "BANK-REF", ConfirmedAt, Arg.Any<CancellationToken>())
            .Returns(Result.Failure(ErrorCodes.NotFound, "No MPay order matches the supplied order id."));
        var sut = BuildSut(store);

        var result = await sut.ConfirmOrderPaymentAsync(
            "ORD-NOPE",
            new MPayConfirmRequest("BANK-REF", ConfirmedAt),
            default);

        result.Should().BeOfType<NotFoundResult>();
    }

    /// <summary>
    /// An empty <c>orderId</c> route segment on the confirm endpoint produces 400 —
    /// matches the details-endpoint behaviour. The store is never called.
    /// </summary>
    [Fact]
    public async Task Confirm_EmptyOrderId_Returns400()
    {
        var store = Substitute.For<IMPayOrderStore>();
        var sut = BuildSut(store);

        var result = await sut.ConfirmOrderPaymentAsync(
            string.Empty,
            new MPayConfirmRequest("BANK-REF", ConfirmedAt),
            default);

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(400);
        await store.DidNotReceiveWithAnyArgs().ConfirmAsync(
            default!, default!, default, default);
    }
}
