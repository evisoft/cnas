using System.Net.Http.Json;
using Cnas.Ps.Contracts;

namespace Cnas.Ps.Web.Components;

/// <summary>
/// Default implementation of <see cref="IClassifierLookup"/>. Issues
/// <c>GET /api/classifiers/{scheme}</c> against the shared <see cref="HttpClient"/>
/// configured for the citizen portal. Transport failures and empty responses
/// degrade to an empty list so the dropdown renders the empty-state
/// container rather than throwing inside the Blazor render loop.
/// </summary>
/// <remarks>
/// Registered as <c>Scoped</c> in DI — the underlying <see cref="HttpClient"/>
/// is per-circuit and the lookup itself is stateless so a per-circuit instance
/// is sufficient.
/// </remarks>
/// <param name="http">Shared HTTP client (typically registered as a singleton at <c>Program.cs</c>).</param>
public sealed class ClassifierLookup(HttpClient http) : IClassifierLookup
{
    /// <summary>Shared HTTP client used to issue the GET.</summary>
    private readonly HttpClient _http = http;

    /// <inheritdoc />
    public async Task<IReadOnlyList<ClassifierRow>> GetActiveAsync(
        string scheme, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(scheme))
        {
            return Array.Empty<ClassifierRow>();
        }

        try
        {
            var rows = await _http
                .GetFromJsonAsync<IReadOnlyList<ClassifierRow>>(
                    $"api/classifiers/{Uri.EscapeDataString(scheme)}",
                    cancellationToken)
                .ConfigureAwait(false);
            return rows ?? Array.Empty<ClassifierRow>();
        }
        catch (HttpRequestException)
        {
            // Degrade to empty so the picker still renders the empty-state container.
            // The error surfaces server-side via the API logs; the Blazor UI gracefully
            // shows "no values" rather than a stack trace.
            return Array.Empty<ClassifierRow>();
        }
        catch (TaskCanceledException)
        {
            return Array.Empty<ClassifierRow>();
        }
    }
}
