using System.Text.Json;
using Cnas.Ps.Application.External;
using Cnas.Ps.Application.Prefill;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Services.Prefill;
using Microsoft.Extensions.Logging;

namespace Cnas.Ps.Infrastructure.MGov;

/// <summary>
/// R0363 — placeholder implementation of <see cref="IRspGateway"/> returning
/// hand-coded sample deltas. Used until the NDA-gated RSP WSDL lands and the real
/// <c>RSP.GetPerson</c>-driven delta computation can be wired through MConnect.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a mock.</b> The real RSP integration depends on MEGA-provided WSDLs and an
/// active mTLS certificate that the open codebase cannot ship. By emitting a stable,
/// hand-coded delta set we let the service / writer / persistence pipelines develop and
/// be tested end-to-end ahead of the NDA work — the gateway interface is the seam where
/// the swap happens.
/// </para>
/// <para>
/// <b>Behaviour.</b> Returns one address delta (<c>street</c>) so a refresh exercises
/// the supersession path on <c>ContributorAddress</c>. Tests inject their own
/// <see cref="IRspGateway"/> double when they need different outcomes — this mock is the
/// production-time default.
/// </para>
/// </remarks>
/// <param name="logger">Structured logger.</param>
public sealed class MockRspGateway(ILogger<MockRspGateway> logger) : IRspGateway, IPrefillSourceAdapter
{
    private readonly ILogger<MockRspGateway> _logger = logger;

    /// <inheritdoc />
    public Task<Result<IReadOnlyList<ProfileRefreshDeltaDto>>> FetchDeltasAsync(string idnp, CancellationToken ct = default)
    {
        _logger.LogInformation("MockRspGateway returning hand-coded delta set for refresh.");
        _ = idnp;
        _ = ct;

        // The hand-coded address payload mirrors ContributorAddressInputDto. The writer
        // applies it via UpdateAddressAsync.
        var addressPayload = JsonSerializer.Serialize(new
        {
            street = "Strada Stefan cel Mare 1",
            city = "Chisinau",
            region = "Chisinau",
            postalCode = "MD2001",
            country = "MD",
        });
        IReadOnlyList<ProfileRefreshDeltaDto> deltas =
        [
            new ProfileRefreshDeltaDto(
                ChildEntityType: "Address",
                FieldName: "street",
                OldValue: null,
                NewValue: "Strada Stefan cel Mare 1",
                PayloadJson: addressPayload),
        ];
        return Task.FromResult(Result<IReadOnlyList<ProfileRefreshDeltaDto>>.Success(deltas));
    }

    /// <inheritdoc />
    public string SourceCode => PrefillSources.Rsp;

    /// <summary>
    /// R0552 / R0562 — hand-coded pre-fill payload matching the existing mock's
    /// address sample. Real RSP wiring (NDA-gated) will populate every RSP-governed
    /// field; today's stub returns the same fields the delta sample exercises so the
    /// merge / conflict / allow-list paths can be tested end-to-end.
    /// </summary>
    public Task<IReadOnlyDictionary<string, string>> FetchPrefillAsync(string idnp, CancellationToken ct)
    {
        _logger.LogInformation("MockRspGateway returning hand-coded pre-fill values.");
        _ = idnp;
        _ = ct;
        IReadOnlyDictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [PrefillFields.Address] = "Strada Stefan cel Mare 1",
            [PrefillFields.City] = "Chisinau",
            [PrefillFields.Region] = "Chisinau",
        };
        return Task.FromResult(values);
    }
}
