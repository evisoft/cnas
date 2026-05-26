using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Captcha;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Security;

namespace Cnas.Ps.Infrastructure.Tests.Security;

/// <summary>
/// R0507 / TOR CF 01.10 — behaviour tests for
/// <see cref="InMemoryCaptchaChallengeService"/>.
/// </summary>
public sealed class CaptchaChallengeServiceTests
{
    private sealed class TestClock : ICnasTimeProvider
    {
        public DateTime UtcNow { get; set; }
            = new DateTime(2026, 5, 24, 10, 0, 0, DateTimeKind.Utc);
    }

    /// <summary>
    /// The challenge code is rendered inside the SVG as one
    /// <c>&lt;text&gt;CODE&lt;/text&gt;</c> element per character. We
    /// re-extract the answer by concatenating the inner text — that mirrors
    /// what a user does when reading the rendered widget.
    /// </summary>
    private static string ExtractAnswerFromSvg(string imageBase64)
    {
        var bytes = Convert.FromBase64String(imageBase64);
        var svg = Encoding.UTF8.GetString(bytes);
        // Each character lands inside `<text ...>X</text>` — strip everything
        // else and concatenate. The matcher is intentionally tight (single
        // character payload) so background rect / svg-element noise can't
        // contaminate the answer.
        var matches = Regex.Matches(svg, @">(?<c>[A-Z0-9])</text>");
        var sb = new StringBuilder();
        foreach (Match m in matches)
        {
            sb.Append(m.Groups["c"].Value);
        }
        return sb.ToString();
    }

    [Fact]
    public async Task Issue_ProducesTokenAndDecodableImage()
    {
        var svc = new InMemoryCaptchaChallengeService(new TestClock());
        var dto = await svc.IssueAsync(CancellationToken.None);
        dto.ChallengeToken.Should().NotBeNullOrWhiteSpace();
        dto.ImageBase64.Should().NotBeNullOrWhiteSpace();
        dto.MimeType.Should().Be(InMemoryCaptchaChallengeService.SvgMimeType);

        var code = ExtractAnswerFromSvg(dto.ImageBase64);
        code.Length.Should().Be(InMemoryCaptchaChallengeService.CodeLength);
    }

    [Fact]
    public async Task Verify_WrongCode_Fails()
    {
        var svc = new InMemoryCaptchaChallengeService(new TestClock());
        var dto = await svc.IssueAsync(CancellationToken.None);

        var result = await svc.VerifyAsync(dto.ChallengeToken, "WRONG0", CancellationToken.None);
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.CaptchaTokenInvalid);
    }

    [Fact]
    public async Task Verify_Expired_Fails()
    {
        var clock = new TestClock();
        var svc = new InMemoryCaptchaChallengeService(clock);
        var dto = await svc.IssueAsync(CancellationToken.None);
        var code = ExtractAnswerFromSvg(dto.ImageBase64);

        // Roll the clock forward past the TTL.
        clock.UtcNow = clock.UtcNow + InMemoryCaptchaChallengeService.DefaultTokenTtl + TimeSpan.FromSeconds(1);

        var result = await svc.VerifyAsync(dto.ChallengeToken, code, CancellationToken.None);
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.CaptchaTokenInvalid);
    }

    [Fact]
    public async Task Verify_Correct_Succeeds_AndTokenIsRecentlyVerified()
    {
        var svc = new InMemoryCaptchaChallengeService(new TestClock());
        var dto = await svc.IssueAsync(CancellationToken.None);
        var code = ExtractAnswerFromSvg(dto.ImageBase64);

        var result = await svc.VerifyAsync(dto.ChallengeToken, code, CancellationToken.None);
        result.IsSuccess.Should().BeTrue();

        var verified = await svc.IsRecentlyVerifiedAsync(dto.ChallengeToken, CancellationToken.None);
        verified.Should().BeTrue();
    }

    [Fact]
    public async Task Verify_MissingFields_ReturnsTokenMissing()
    {
        var svc = new InMemoryCaptchaChallengeService(new TestClock());
        var result = await svc.VerifyAsync(null, "ABC", CancellationToken.None);
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.CaptchaTokenMissing);
    }

    /// <summary>
    /// R0507 — concurrency contract. Two parallel Verify calls for the same
    /// (token, answer) tuple must produce exactly ONE success — the other
    /// loses the CAS race and observes the verified entry on its next iter
    /// returning <see cref="ErrorCodes.CaptchaTokenInvalid"/>. Pins the
    /// TryUpdate atomicity that closed the TOCTOU on
    /// <c>_entries[token] = entry with { ... }</c>.
    /// </summary>
    [Fact]
    public async Task Verify_TwoConcurrentVerifies_OnlyOneSucceeds()
    {
        var svc = new InMemoryCaptchaChallengeService(new TestClock());
        var dto = await svc.IssueAsync(CancellationToken.None);
        var code = ExtractAnswerFromSvg(dto.ImageBase64);

        // Fan out 16 racers so a thread-pool with > 2 workers actually
        // contends on the entry. The CAS guarantees one winner across the
        // whole fan-out.
        const int racers = 16;
        var results = new ConcurrentBag<Result>();
        var barrier = new Barrier(racers);
        var tasks = Enumerable.Range(0, racers).Select(_ => Task.Run(async () =>
        {
            barrier.SignalAndWait();
            var r = await svc.VerifyAsync(dto.ChallengeToken, code, CancellationToken.None);
            results.Add(r);
        })).ToArray();
        await Task.WhenAll(tasks);

        results.Count(r => r.IsSuccess).Should().Be(1,
            "exactly one concurrent verify must win the CAS — the rest observe the stamped entry");
        results.Count(r => r.IsFailure && r.ErrorCode == ErrorCodes.CaptchaTokenInvalid).Should().Be(racers - 1);
    }

    /// <summary>
    /// R0507 — once a token has been verified, the gate flips it via
    /// <c>ConsumeAsync</c>. The first consume succeeds; the second returns
    /// <see cref="ErrorCodes.CaptchaAlreadyConsumed"/>. Pins the one-shot
    /// invariant — verified tokens are not replayable.
    /// </summary>
    [Fact]
    public async Task Consume_FirstSucceeds_SecondReturnsAlreadyConsumed()
    {
        var svc = new InMemoryCaptchaChallengeService(new TestClock());
        var dto = await svc.IssueAsync(CancellationToken.None);
        var code = ExtractAnswerFromSvg(dto.ImageBase64);
        (await svc.VerifyAsync(dto.ChallengeToken, code, CancellationToken.None)).IsSuccess.Should().BeTrue();

        var first = await svc.ConsumeAsync(dto.ChallengeToken, CancellationToken.None);
        var second = await svc.ConsumeAsync(dto.ChallengeToken, CancellationToken.None);

        first.IsSuccess.Should().BeTrue();
        second.IsFailure.Should().BeTrue();
        second.ErrorCode.Should().Be(ErrorCodes.CaptchaAlreadyConsumed);
    }

    /// <summary>
    /// R0507 — consuming an un-verified token must fail with
    /// <see cref="ErrorCodes.CaptchaTokenInvalid"/>. The consume API is only
    /// legitimate for already-verified entries; calling it on a fresh
    /// challenge is a controller-side wiring bug.
    /// </summary>
    [Fact]
    public async Task Consume_UnverifiedToken_ReturnsTokenInvalid()
    {
        var svc = new InMemoryCaptchaChallengeService(new TestClock());
        var dto = await svc.IssueAsync(CancellationToken.None);

        var result = await svc.ConsumeAsync(dto.ChallengeToken, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.CaptchaTokenInvalid);
    }

    /// <summary>
    /// R0507 — null / whitespace token returns
    /// <see cref="ErrorCodes.CaptchaTokenMissing"/> from ConsumeAsync — the
    /// same contract as VerifyAsync so the API surface is symmetrical.
    /// </summary>
    [Fact]
    public async Task Consume_MissingToken_ReturnsTokenMissing()
    {
        var svc = new InMemoryCaptchaChallengeService(new TestClock());

        var result = await svc.ConsumeAsync(null, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.CaptchaTokenMissing);
    }

    /// <summary>
    /// R0507 — concurrent ConsumeAsync racers for the SAME verified token
    /// must resolve to exactly one success and N-1 ALREADY_CONSUMED. Pins
    /// the CAS contract on the consume side independently of the verify
    /// race above (the two CAS loops protect different transitions).
    /// </summary>
    [Fact]
    public async Task Consume_TwoConcurrentConsumes_OnlyOneSucceeds()
    {
        var svc = new InMemoryCaptchaChallengeService(new TestClock());
        var dto = await svc.IssueAsync(CancellationToken.None);
        var code = ExtractAnswerFromSvg(dto.ImageBase64);
        (await svc.VerifyAsync(dto.ChallengeToken, code, CancellationToken.None)).IsSuccess.Should().BeTrue();

        const int racers = 16;
        var results = new ConcurrentBag<Result>();
        var barrier = new Barrier(racers);
        var tasks = Enumerable.Range(0, racers).Select(_ => Task.Run(async () =>
        {
            barrier.SignalAndWait();
            var r = await svc.ConsumeAsync(dto.ChallengeToken, CancellationToken.None);
            results.Add(r);
        })).ToArray();
        await Task.WhenAll(tasks);

        results.Count(r => r.IsSuccess).Should().Be(1);
        results.Count(r => r.IsFailure && r.ErrorCode == ErrorCodes.CaptchaAlreadyConsumed)
            .Should().Be(racers - 1);
    }

    /// <summary>
    /// R0507 — IsRecentlyVerifiedAsync must return false for a consumed
    /// entry as defence-in-depth — even if a callsite forgets to call
    /// ConsumeAsync, a consumed token cannot pass the recently-verified
    /// gate.
    /// </summary>
    [Fact]
    public async Task IsRecentlyVerified_AfterConsume_ReturnsFalse()
    {
        var svc = new InMemoryCaptchaChallengeService(new TestClock());
        var dto = await svc.IssueAsync(CancellationToken.None);
        var code = ExtractAnswerFromSvg(dto.ImageBase64);
        (await svc.VerifyAsync(dto.ChallengeToken, code, CancellationToken.None)).IsSuccess.Should().BeTrue();
        (await svc.IsRecentlyVerifiedAsync(dto.ChallengeToken, CancellationToken.None)).Should().BeTrue();

        (await svc.ConsumeAsync(dto.ChallengeToken, CancellationToken.None)).IsSuccess.Should().BeTrue();

        (await svc.IsRecentlyVerifiedAsync(dto.ChallengeToken, CancellationToken.None)).Should().BeFalse();
    }
}
