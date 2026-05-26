using Cnas.Ps.Application.Interop.Batch;
using Cnas.Ps.Infrastructure.Services.Interop.Batch;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.Tests.Interop.Batch;

/// <summary>
/// R1710 / TOR INT 002 — tests for <see cref="HmacSha256BatchResponseSigner"/>.
/// </summary>
public sealed class HmacSha256BatchResponseSignerTests
{
    private static HmacSha256BatchResponseSigner NewSigner()
    {
        var opts = Options.Create(new BatchResponseSigningOptions
        {
            HmacKeyBase64 = Convert.ToBase64String(new byte[] { 0xA, 0xB, 0xC, 0xD, 0xE, 0xF }),
        });
        return new HmacSha256BatchResponseSigner(opts);
    }

    /// <summary>R1710 — Sign+Verify round-trip yields true for the same payload.</summary>
    [Fact]
    public async Task SignVerify_RoundTrip_Succeeds()
    {
        var signer = NewSigner();
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var sig = await signer.SignAsync(payload);
        var ok = await signer.VerifyAsync(payload, sig);
        ok.Should().BeTrue();
    }

    /// <summary>R1710 — Tampered payload bytes return false on verify.</summary>
    [Fact]
    public async Task Verify_TamperedBytes_ReturnsFalse()
    {
        var signer = NewSigner();
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var sig = await signer.SignAsync(payload);
        var tampered = new byte[] { 1, 2, 3, 4, 6 };
        var ok = await signer.VerifyAsync(tampered, sig);
        ok.Should().BeFalse();
    }
}
