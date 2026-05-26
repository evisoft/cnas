namespace Cnas.Ps.Infrastructure.Workflow;

/// <summary>
/// Configuration for the Operaton (Camunda 7-compatible) workflow engine adapter.
/// Bound from the <c>Workflow</c> section of application configuration.
/// </summary>
/// <remarks>
/// In dev/test environments the base URL is typically left empty, in which case all
/// engine calls short-circuit to <c>INTERNAL_ERROR</c> without producing any HTTP traffic.
/// In production, Basic auth is the recommended transport because Operaton's default REST
/// distribution accepts only Basic; mTLS termination is delegated to the API gateway.
/// </remarks>
public sealed class WorkflowOptions
{
    /// <summary>Configuration section name used by the host bindings.</summary>
    public const string SectionName = "Workflow";

    /// <summary>Operaton engine base URL, e.g. <c>https://operaton.cnas.gov.md</c>. Empty disables the engine.</summary>
    public string OperatonBaseUrl { get; set; } = string.Empty;

    /// <summary>HTTP Basic auth username; when null/empty no <c>Authorization</c> header is sent.</summary>
    public string? OperatonBasicAuthUser { get; set; }

    /// <summary>HTTP Basic auth password; paired with <see cref="OperatonBasicAuthUser"/>.</summary>
    public string? OperatonBasicAuthPassword { get; set; }

    /// <summary>Per-call HTTP timeout. The <see cref="System.Net.Http.HttpClient"/> timeout is configured to this value.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// R0942 / TOR §10.1 — when <c>true</c> the
    /// <c>IRefusedPensionFallbackCascade</c> automatically opens an
    /// <c>AlocatieSociala</c> follow-up application for the same Solicitant
    /// whenever a pension decision lands in
    /// <c>ApplicationStatus.Rejected</c>. When <c>false</c> the cascade is a
    /// no-op (operators must open the social-allowance file manually).
    /// Default: <c>true</c>.
    /// </summary>
    public bool AutoFallbackToSocialAllowance { get; set; } = true;

    /// <summary>
    /// R0942 — stable passport code of the social-allowance service used as the
    /// cascade target. Defaults to <c>SP-3.2-N-SOCIAL-ALLOWANCE-ELDERLY</c> but
    /// operators can repoint the cascade to a different passport without
    /// recompiling.
    /// </summary>
    public string SocialAllowancePassportCode { get; set; } = "SP-3.2-N-SOCIAL-ALLOWANCE-ELDERLY";
}
