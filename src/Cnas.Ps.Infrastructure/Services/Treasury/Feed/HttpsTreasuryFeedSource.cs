using Cnas.Ps.Application.Treasury.Feed;
using Cnas.Ps.Core.Common;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.Services.Treasury.Feed;

/// <summary>
/// R1810 / TOR BP 1.2-I — placeholder HTTPS implementation of
/// <see cref="ITreasuryFeedSource"/>. Returns a deterministic
/// <c>TREASURY_FEED.NOT_CONFIGURED</c> failure when no base URL is
/// configured. Production iterations replace the body with a real HTTPS /
/// SFTP fetch.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a placeholder.</b> The Treasury endpoint will most likely be SFTP
/// in production but a stable HTTPS path is the simplest first iteration.
/// Shipping a deterministic failure here lets operators wire the configuration
/// without leaving the system open to surprise dependencies on day one.
/// </para>
/// <para>
/// <b>No NotImplementedException.</b> The discipline rules require a
/// well-typed <c>Result.Failure</c>, never a thrown exception — operators
/// see the failure on the audit log instead of paging on the dead-letter
/// queue.
/// </para>
/// </remarks>
public sealed class HttpsTreasuryFeedSource : ITreasuryFeedSource
{
    private readonly IOptions<TreasuryFeedOptions> _options;

    /// <summary>Constructs the placeholder source.</summary>
    /// <param name="options">Bound <see cref="TreasuryFeedOptions"/> envelope.</param>
    public HttpsTreasuryFeedSource(IOptions<TreasuryFeedOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <inheritdoc />
    public Task<Result<TreasuryFeedFetchOutcome>> FetchAsync(
        DateOnly feedDate,
        CancellationToken cancellationToken = default)
    {
        // This iteration always returns the deterministic configuration failure.
        // A future iteration will:
        //   1. Build the per-date URL from BaseUrl + feedDate.
        //   2. Open an HttpClient, GET the file, buffer to a byte[].
        //   3. Compute SHA-256 and populate the outcome envelope.
        // Until then, refuse loudly so operators see the misconfiguration on
        // the import row's FailureReason and audit log.
        if (string.IsNullOrWhiteSpace(_options.Value.HttpsBaseUrl))
        {
            return Task.FromResult(Result<TreasuryFeedFetchOutcome>.Failure(
                ITreasuryFeedImporter.SourceNotConfiguredCode,
                "TREASURY_FEED.HttpsBaseUrl is not configured."));
        }

        // Even when configured, this iteration declines to fetch — the real
        // implementation lands in a follow-up iteration. Keeping the failure
        // explicit makes the seam visible during code review and operations
        // dashboards never silently fail-open.
        return Task.FromResult(Result<TreasuryFeedFetchOutcome>.Failure(
            ITreasuryFeedImporter.SourceNotConfiguredCode,
            "HTTPS Treasury feed source is not wired yet (placeholder)."));
    }
}
