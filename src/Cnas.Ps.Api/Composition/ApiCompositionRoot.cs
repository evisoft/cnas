using Cnas.Ps.Api.Health;
using Cnas.Ps.Api.Middleware;
using Cnas.Ps.Api.Security;
using Cnas.Ps.Application;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Infrastructure;
using Cnas.Ps.Infrastructure.MGov;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;

namespace Cnas.Ps.Api.Composition;

/// <summary>
/// API-layer DI composition root. Wires Application + Infrastructure services,
/// Serilog, OpenAPI, authentication, rate limiting, and health checks per TOR §4.9 (security)
/// and §4.5 (performance).
/// </summary>
public static class ApiCompositionRoot
{
    /// <summary>Shared tag set for every MGov-platform readiness probe.</summary>
    private static readonly string[] MGovReadyTags = ["ready", "mgov"];

    /// <summary>Shared tag set for the workflow-engine readiness probe.</summary>
    private static readonly string[] WorkflowReadyTags = ["ready", "workflow"];

    /// <summary>Shared tag set for the object-storage readiness probe.</summary>
    private static readonly string[] StorageReadyTags = ["ready", "storage"];

    /// <summary>Shared tag set for the database readiness probe.</summary>
    private static readonly string[] DbReadyTags = ["ready", "db"];

    /// <summary>
    /// Configures Serilog as the host logger (per CLAUDE.md §6.1 — structured logging)
    /// and registers the OpenTelemetry tracing + metrics pipeline (CLAUDE.md Phase 6).
    /// Correlation IDs are added via middleware in <see cref="UseCnasApiPipeline"/>.
    /// </summary>
    /// <remarks>
    /// This convenience overload first wires Serilog onto the <see cref="IHostBuilder"/>
    /// (legacy logging path) then delegates OpenTelemetry registration to the
    /// <see cref="AddCnasObservability(IServiceCollection, IConfiguration)"/> extension.
    /// </remarks>
    public static void AddCnasObservability(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Host.UseSerilog((ctx, lc) => lc
            .ReadFrom.Configuration(ctx.Configuration)
            .Enrich.FromLogContext()
            .Enrich.WithEnvironmentName()
            .Enrich.WithMachineName());

        builder.Services.AddCnasObservability(builder.Configuration);
    }

    /// <summary>
    /// Registers the OpenTelemetry tracing and metrics pipeline (traces +
    /// metrics) on the supplied service collection. Reads
    /// <see cref="ObservabilityOptions"/> from <c>Cnas:Observability</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When <see cref="ObservabilityOptions.OtlpEndpoint"/> is null or empty,
    /// no exporter is registered. The OTel SDK itself is still wired so any
    /// code that uses <see cref="System.Diagnostics.ActivitySource"/> or
    /// <see cref="System.Diagnostics.Metrics.Meter"/> compiles and runs — it
    /// just doesn't ship any data over the network. This keeps dev/test
    /// environments fast and quiet without forcing every consumer to
    /// branch on configuration.
    /// </para>
    /// <para>
    /// The method is idempotent: calling it twice on the same collection is
    /// safe because <see cref="OpenTelemetryServicesExtensions.AddOpenTelemetry(IServiceCollection)"/>
    /// returns the same builder instance and the instrumentation/exporter
    /// registrations accept duplicate sources without throwing.
    /// </para>
    /// <para>
    /// EF Core spans deliberately set <c>SetDbStatementForText = false</c>
    /// so personally-identifying information embedded in SQL literals never
    /// reaches the collector (parameterised queries with PII bound as
    /// parameters are also stripped automatically).
    /// </para>
    /// </remarks>
    /// <param name="services">Service collection to extend.</param>
    /// <param name="configuration">Application configuration to bind options from.</param>
    /// <returns>The same service collection for fluent chaining.</returns>
    public static IServiceCollection AddCnasObservability(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Bind ObservabilityOptions so consumers can inject IOptions<ObservabilityOptions>
        // if they want to vary behaviour at runtime (e.g. health checks reporting whether
        // observability is active).
        services.AddOptions<ObservabilityOptions>()
            .Bind(configuration.GetSection(ObservabilityOptions.SectionName));

        // Snapshot the options once at registration time. The OTel SDK does not support
        // runtime reconfiguration anyway — the exporter pipeline is built once during
        // DI build-out, so reading IConfiguration directly here is correct.
        var opts = configuration.GetSection(ObservabilityOptions.SectionName)
            .Get<ObservabilityOptions>() ?? new ObservabilityOptions();

        services.AddOpenTelemetry()
            .ConfigureResource(r => r
                .AddService(opts.ServiceName, serviceVersion: opts.ServiceVersion)
                .AddAttributes(new[]
                {
                    new KeyValuePair<string, object>("deployment.environment", opts.Environment),
                }))
            .WithTracing(t =>
            {
                // Wildcard subscription so additional sub-systems (Cnas.Ps.Infrastructure,
                // Cnas.Ps.Application, ...) can plug new ActivitySources in without
                // touching the composition root.
                t.AddSource("Cnas.Ps.*");
                t.AddAspNetCoreInstrumentation();
                t.AddHttpClientInstrumentation();
                t.AddEntityFrameworkCoreInstrumentation(o =>
                {
                    // Never serialise raw SQL text into spans — citizens' personal data
                    // (IDNP, names) sometimes appears in WHERE clauses and would leak
                    // into the collector otherwise.
                    o.SetDbStatementForText = false;
                });

                if (!string.IsNullOrWhiteSpace(opts.OtlpEndpoint))
                {
                    t.AddOtlpExporter(o => o.Endpoint = new Uri(opts.OtlpEndpoint));
                }

                if (opts.EnableConsoleExporter)
                {
                    t.AddConsoleExporter();
                }
            })
            .WithMetrics(m =>
            {
                // R0040 — the wildcard already subscribes to every Cnas.Ps.* meter
                // including the new CnasMeter ("Cnas.Ps.Subsystems"). The explicit
                // AddMeter call documents the contract and survives any future
                // refactor that narrows the wildcard.
                m.AddMeter("Cnas.Ps.*");
                m.AddMeter(Cnas.Ps.Infrastructure.Observability.CnasMeter.MeterName);
                m.AddAspNetCoreInstrumentation();
                m.AddHttpClientInstrumentation();
                m.AddRuntimeInstrumentation();

                if (!string.IsNullOrWhiteSpace(opts.OtlpEndpoint))
                {
                    m.AddOtlpExporter(o => o.Endpoint = new Uri(opts.OtlpEndpoint));
                }

                if (opts.EnableConsoleExporter)
                {
                    m.AddConsoleExporter();
                }
            });

        return services;
    }

    /// <summary>
    /// Registers Application + Infrastructure services, controllers, OpenAPI,
    /// authentication, authorization, and health checks for the API host.
    /// </summary>
    public static IServiceCollection AddCnasApi(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddCnasApplication();
        services.AddCnasInfrastructure(configuration);

        // R2164 / TOR §15.4 INT 005 — WSDL portal service. Singleton: the WSDL output is
        // deterministic over the (fixed) controller assembly so a single instance can
        // safely serve every request. Scans the Cnas.Ps.Api assembly (this assembly).
        services.AddSingleton<Cnas.Ps.Application.UseCases.IWsdlPortalService>(_ =>
            new Cnas.Ps.Infrastructure.Services.WsdlPortalService(
                typeof(ApiCompositionRoot).Assembly));

        // R2135 / TOR §15.2 ARH 026 — XSD export service. Stateless reflection
        // over the curated DTO allow-list; safe as a singleton.
        services.AddSingleton<Cnas.Ps.Application.UseCases.IXsdExportService,
            Cnas.Ps.Infrastructure.Services.XsdExportService>();

        // Keep `Async` in action names so `nameof(GetAsync)` in `CreatedAtAction(...)` calls
        // matches the route-discovery name. The MVC default (SuppressAsyncSuffixInActionNames=true)
        // strips `Async`, which silently breaks every CreatedAtAction(nameof(GetAsync), ...) site
        // (Applications, Contributors, InsuredPersons): the DB writes commit but the 201 response
        // throws InvalidOperationException because MVC can't find a route matching "GetAsync".
        services.AddControllers(opts => opts.SuppressAsyncSuffixInActionNames = false);
        services.AddOpenApi();
        services.AddProblemDetails();
        services.AddHttpContextAccessor();
        services.AddScoped<ICallerContext, HttpCallerContext>();
        services.AddOptions<CallbackSignatureOptions>()
            .Bind(configuration.GetSection(CallbackSignatureOptions.SectionName));
        services.AddSingleton<ICallbackSignatureVerifier, CallbackSignatureVerifier>();
        // CLAUDE.md §5.3 — rate limiting. Four named partition policies (Anonymous,
        // Callback, Upload, Authenticated) plus a process-wide concurrency ceiling.
        // See RateLimitingComposition and RateLimitingOptions for the policy table
        // and the IP-trust-chain rules.
        services.AddCnasRateLimiting(configuration);

        // CORS — the WebAssembly client lives at a separate origin. The list of allowed
        // origins is configured via Cors:AllowedOrigins so production can lock it down
        // to the MCloud-published domain only. In non-Development / non-Test environments
        // a missing / empty list is treated as a hard misconfiguration: silently falling
        // back to localhost would mean the production cluster is operating with a
        // browser-only-on-the-loopback-host CORS allowlist, which is operationally
        // surprising and could mask a forgotten Cors__AllowedOrigins__0 env var.
        // Development / Test / Testing still get the localhost default so `dotnet run`
        // and the in-process E2E fixture work without explicit configuration. The
        // environment name is read directly from configuration to avoid a
        // services.BuildServiceProvider() round-trip (the same parallel-container
        // anti-pattern RateLimitingComposition explicitly avoids).
        var environmentName = configuration["ASPNETCORE_ENVIRONMENT"]
                              ?? configuration["DOTNET_ENVIRONMENT"]
                              ?? Environments.Production;
        var corsBypassed = string.Equals(environmentName, Environments.Development, StringComparison.OrdinalIgnoreCase)
                           || string.Equals(environmentName, "Test", StringComparison.OrdinalIgnoreCase)
                           || string.Equals(environmentName, "Testing", StringComparison.OrdinalIgnoreCase);
        var allowedOrigins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>();
        if ((allowedOrigins is null || allowedOrigins.Length == 0) && !corsBypassed)
        {
            throw new InvalidOperationException(
                "Cors:AllowedOrigins must be configured in non-Development environments. " +
                "Set it via Cors__AllowedOrigins__0 env var (or equivalent secret-store path).");
        }
        allowedOrigins ??= ["http://localhost:8081"];
        services.AddCors(opts => opts.AddPolicy("CnasWeb", p => p
            .WithOrigins(allowedOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials()));

        services.AddCnasAuthentication(configuration);
        // Tiered RBAC policies (CnasUser ⊂ CnasDecider ⊂ CnasAdmin, plus standalone
        // CnasTechAdmin). Defined as constants on AuthorizationComposition so controllers
        // never hard-code role strings.
        services.AddCnasAuthorization();

        // Per-dependency readiness checks. Every check is tagged "ready" so the
        // /health/ready endpoint can filter precisely to the dependency-bearing
        // probes (the /health/live endpoint excludes all checks via Predicate = false).
        // MGov probes are tagged "mgov", workflow as "workflow", storage as "storage",
        // and Postgres as "db" so dashboards can group at a glance.
        // R2175 / R2134 — probe + per-endpoint replica health check. The probe is
        // stateless and thread-safe (singleton); the check is constructed with the
        // two connection strings resolved from configuration so a single instance
        // serves every readiness sweep AND the dedicated /api/health/database
        // controller. Both registrations are deliberate prerequisites for the
        // .AddCheck<DatabaseReplicaHealthCheck> line below so the resolver does
        // not have to re-read configuration on every probe.
        services.AddSingleton<IDatabaseConnectionProbe, NpgsqlConnectionProbe>();
        services.AddSingleton(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            var probe = sp.GetRequiredService<IDatabaseConnectionProbe>();
            var primary = config.GetConnectionString("Postgres") ?? string.Empty;
            var replica = config.GetConnectionString("PostgresReadReplica");
            return new DatabaseReplicaHealthCheck(probe, primary, replica);
        });

        services.AddHealthChecks()
            .AddCheck<MSignHealthCheck>("mgov.msign", tags: MGovReadyTags)
            .AddCheck<MPayHealthCheck>("mgov.mpay", tags: MGovReadyTags)
            .AddCheck<MConnectHealthCheck>("mgov.mconnect", tags: MGovReadyTags)
            .AddCheck<MNotifyHealthCheck>("mgov.mnotify", tags: MGovReadyTags)
            .AddCheck<MLogHealthCheck>("mgov.mlog", tags: MGovReadyTags)
            // MPower removed — consumed via MPass SAML claims, not a standalone HTTP service.
            .AddCheck<MConnectEventsHealthCheck>("mgov.mconnect.events", tags: MGovReadyTags)
            .AddCheck<MDocsHealthCheck>("mgov.mdocs", tags: MGovReadyTags)
            .AddCheck<WorkflowEngineHealthCheck>("workflow.operaton", tags: WorkflowReadyTags)
            .AddCheck<MinioHealthCheck>("storage.minio", tags: StorageReadyTags)
            .AddNpgSql(
                sp => sp.GetRequiredService<IConfiguration>().GetConnectionString("Postgres")!,
                name: "db.postgres",
                tags: DbReadyTags)
            // R2175 / R2134 — replica readiness probe contributing to /health/ready.
            // Pulls the pre-built singleton so the same instance backs both the
            // generic readiness sweep and /api/health/database.
            .AddCheck<DatabaseReplicaHealthCheck>("db.postgres.replica", tags: DbReadyTags);

        return services;
    }

    /// <summary>
    /// Wires the request pipeline: HTTPS redirect, rate limiting, auth, controllers,
    /// OpenAPI document and health endpoint.
    /// </summary>
    public static WebApplication UseCnasApiPipeline(this WebApplication app)
    {
        // CLAUDE.md / SEC 057 — must run FIRST. Catches anything that escapes any other
        // middleware (Serilog request logging, HSTS, HTTPS redirect, CORS, auth, rate
        // limiter, routing, MVC, ...), logs the full stack trace server-side, and writes
        // a sanitised ProblemDetails 500 to the wire. See UnhandledExceptionMiddleware.
        app.UseMiddleware<UnhandledExceptionMiddleware>();

        app.UseSerilogRequestLogging();

        if (!app.Environment.IsDevelopment())
        {
            app.UseHsts();
            app.UseHttpsRedirection();
        }

        app.UseRouting();
        app.UseCors("CnasWeb");
        // Authentication MUST run before the rate limiter so user-id-partitioned
        // policies (Authenticated, Upload) see the populated HttpContext.User and
        // can bucket by stable principal id rather than per-request "unknown".
        // See RateLimitingComposition.ResolveUserPartitionKey for the contract.
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();

        // R0228 / TOR SEC 033 — sensitivity-marking response headers. Registered after
        // authentication/authorization so the matched endpoint is available via
        // HttpContext.GetEndpoint(), and BEFORE MapControllers so the OnStarting hook
        // attaches before MVC begins flushing the response body.
        app.UseSensitivityHeaders();

        // OpenAPI document — used by Swagger UI and contract testing. Exempted from
        // rate limiting so SREs / clients pulling the schema during deploy
        // verifications never get 429'd.
        app.MapOpenApi().RequireAuthorization(AuthorizationComposition.CnasTechAdmin);
        app.MapControllers();

        // Liveness — pure process-alive ping. Predicate excludes every registered check
        // (Predicate = _ => false) so this endpoint stays green even when Postgres or MGov
        // are flapping. Kubernetes liveness probes wire here to avoid pod-restart storms.
        // Rate-limit disabled because a flap on the limiter must never knock pods out of
        // rotation — kubelet probes are infrequent (every few seconds) and unauthenticated
        // so they would otherwise share a single IP partition with the entire cluster.
        app.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = _ => false,
            ResponseWriter = HealthCheckResponses.WriteJsonAsync,
        }).DisableRateLimiting();

        // Readiness — only checks tagged "ready" (i.e. all dependency probes) run.
        // Orchestrators wire load-balancer drain logic here so a flapping dependency
        // removes the pod from the rotation without restarting the process. Same
        // free-flow rationale as /health/live.
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = c => c.Tags.Contains("ready"),
            ResponseWriter = HealthCheckResponses.WriteJsonAsync,
        }).DisableRateLimiting();

        // Legacy /health — preserved for backwards compatibility with the previously
        // scaffolded endpoint. Behaves identically to /health/ready (full dependency sweep).
        app.MapHealthChecks("/health", new HealthCheckOptions
        {
            Predicate = c => c.Tags.Contains("ready"),
            ResponseWriter = HealthCheckResponses.WriteJsonAsync,
        }).DisableRateLimiting();

        return app;
    }
}
