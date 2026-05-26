using System.Net.Http;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Audit;
using Cnas.Ps.Application.BulkActions;
using Cnas.Ps.Application.BulkActions.Operations;
using Cnas.Ps.Application.External;
using Cnas.Ps.Application.Help;
using Cnas.Ps.Application.Localization;
using Cnas.Ps.Application.WorkflowAcl;
using Cnas.Ps.Application.WorkflowNotifications;
using Cnas.Ps.Application.WorkflowRules;
using Cnas.Ps.Application.WorkflowTasks;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Common;
using Cnas.Ps.Infrastructure.Documents.Templates;
using Cnas.Ps.Infrastructure.Jobs;
using Cnas.Ps.Infrastructure.MGov;
using Cnas.Ps.Infrastructure.MGov.External;
using Cnas.Ps.Infrastructure.Observability;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Secrets;
using Cnas.Ps.Infrastructure.Security;
using Cnas.Ps.Infrastructure.Services;
using Cnas.Ps.Infrastructure.Storage;
using Cnas.Ps.Infrastructure.Workflow;
using Cnas.Ps.Application.UseCases;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Minio;
using Npgsql;

namespace Cnas.Ps.Infrastructure;

/// <summary>
/// Infrastructure-layer service registration. Wires Sqid encoder, persistence,
/// MinIO storage, MGov adapters, and background jobs.
/// </summary>
public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>
    /// Adds Infrastructure dependencies to <paramref name="services"/>, binding options
    /// from <paramref name="configuration"/>. Persistence and external integrations are
    /// registered as scoped or singleton per CLAUDE.md §2.3.
    /// </summary>
    public static IServiceCollection AddCnasInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<SqidOptions>()
            .Bind(configuration.GetSection(SqidOptions.SectionName))
            .Validate(o => !string.IsNullOrWhiteSpace(o.Alphabet) && o.MinLength >= 4,
                "Sqids:Alphabet must be non-empty and Sqids:MinLength must be ≥ 4.")
            .ValidateOnStart();

        services.AddSingleton<ICnasTimeProvider, SystemTimeProvider>();
        services.AddSingleton<ISqidService, SqidService>();

        // R0228 / TOR SEC 033 — sensitivity-marking resolver + audit forwarder.
        // The resolver is stateless and caches its per-type snapshot in a
        // process-static ConcurrentDictionary, so singleton is the right lifetime.
        // The audit forwarder depends on the scoped IAuditService, so it must be
        // scoped itself — the middleware resolves it per request.
        services.AddSingleton<Cnas.Ps.Application.Sensitivity.ISensitivityResolver,
            Cnas.Ps.Infrastructure.Security.SensitivityResolver>();
        services.AddScoped<Cnas.Ps.Application.Sensitivity.ISensitivityAuditService,
            Cnas.Ps.Infrastructure.Security.SensitivityAuditService>();

        // Field-level encryption (CLAUDE.md §5.7 / TOR SEC 035). The master key is
        // sourced from the secrets manager (Vault / k8s Secret / MCloud KMS) per
        // CLAUDE.md §1.8 — NEVER from appsettings.json. When the key is absent we
        // register a sentinel that throws on first use (fails loud) rather than
        // silently writing plaintext.
        services.AddOptions<FieldEncryptionOptions>()
            .Bind(configuration.GetSection(FieldEncryptionOptions.SectionName));
        var encryptionKeyConfigured = !string.IsNullOrWhiteSpace(
            configuration[$"{FieldEncryptionOptions.SectionName}:Key"]);
        if (encryptionKeyConfigured)
        {
            services.AddSingleton<IFieldEncryptor, AesFieldEncryptor>();
        }
        else
        {
            services.AddSingleton<IFieldEncryptor, MissingKeyFieldEncryptor>();
        }

        // Password hashing (CLAUDE.md §5.3 / TOR SEC 014 / R0052). Argon2id with OWASP 2024
        // parameters; produces PHC-formatted strings ready for the UserProfile.LocalPasswordHash
        // column. Stateless and thread-safe — singleton lifetime. Used only by the local
        // Utilizator autorizat credential fallback (R0051); citizens authenticate via MPass SAML.
        services.AddSingleton<IPasswordHasher, Argon2idPasswordHasher>();

        // R0053 / SEC 018 — JWT access token + opaque refresh token pipeline. The JWT
        // issuer is stateless and thread-safe (singleton); the refresh-token service
        // holds a per-request DbContext (scoped). JwtOptions is validated at start-up
        // ONLY when a Jwt section is actually present in configuration: SigningKey MUST
        // decode to ≥32 bytes (HS256 requires 256-bit key material) and both Issuer +
        // Audience MUST be non-empty. A failure here surfaces as a loud
        // OptionsValidationException at host start-up rather than on the first request.
        // The gate avoids a startup crash in environments that do not enable the JWT
        // bearer scheme — AuthenticationComposition.cs only registers AddJwtBearer when
        // jwt.SigningKey / Issuer / Audience are all non-empty, so the two surfaces
        // share the same "JWT is opt-in" contract.
        if (configuration.GetSection(JwtOptions.SectionName).Exists())
        {
            services.AddOptions<JwtOptions>()
                .Bind(configuration.GetSection(JwtOptions.SectionName))
                .Validate(static o =>
                    !string.IsNullOrWhiteSpace(o.Issuer)
                    && !string.IsNullOrWhiteSpace(o.Audience)
                    && IsValidSigningKey(o.SigningKey),
                    "Jwt:Issuer and Jwt:Audience are required, and Jwt:SigningKey MUST be a base64 string decoding to ≥32 bytes.")
                .ValidateOnStart();
        }
        else
        {
            // Section absent — register a no-op options binding so any code that injects
            // IOptions<JwtOptions> still resolves cleanly. The JWT bearer scheme is NOT
            // registered in this branch (see AuthenticationComposition), so no JWT
            // traffic is authenticated until configuration is populated and the host
            // restarts.
            services.AddOptions<JwtOptions>()
                .Configure(static _ => { });
        }
        services.AddSingleton<IJwtTokenIssuer, JwtTokenIssuer>();
        services.AddScoped<IRefreshTokenService, RefreshTokenService>();

        // R0035 — CAPTCHA verifier for the anonymous public surface (UC01 / UC02). Bound
        // from Cnas:Captcha:Turnstile; SecretKey + SiteKey are required unless
        // BypassForTesting is true (used by integration / E2E fixtures so the suite
        // never hits Cloudflare from CI). Production / staging config sources SecretKey
        // from the secrets manager per CLAUDE.md §1.8 — never from appsettings.json.
        // The verifier is registered as scoped to match the rest of the per-request
        // service graph; the underlying HttpClient is managed by IHttpClientFactory
        // (named "turnstile") so socket lifetimes are correct under load.
        services.AddOptions<TurnstileOptions>()
            .Bind(configuration.GetSection(TurnstileOptions.SectionName))
            .Validate(static o =>
                o.BypassForTesting
                || (!string.IsNullOrWhiteSpace(o.SecretKey) && !string.IsNullOrWhiteSpace(o.SiteKey)),
                "Cnas:Captcha:Turnstile:SecretKey and :SiteKey are required unless BypassForTesting is true.")
            .Validate(o =>
                !IsProductionEnvironment(configuration) || !o.BypassForTesting,
                "Cnas:Captcha:Turnstile:BypassForTesting must be false in Production.")
            .ValidateOnStart();
        services.AddHttpClient(TurnstileCaptchaVerifier.ClientName, c =>
        {
            c.Timeout = TimeSpan.FromSeconds(10); // upper bound — per-call timeout is in TurnstileOptions.Timeout.
            c.DefaultRequestHeaders.UserAgent.ParseAdd("CNAS-PS/1.0");
        });
        services.AddScoped<ICaptchaVerifier, TurnstileCaptchaVerifier>();

        // Field-level deterministic hashing (CLAUDE.md §5.7 — restores equality lookups on
        // columns that are encrypted at rest). The salt is sourced from the secrets manager
        // alongside the encryption master key — both share the same blast radius. When the
        // salt is absent we register a sentinel that throws on first use (fails loud) rather
        // than silently emitting unsalted SHA-256, which would defeat the brute-force
        // resistance the primitive exists to provide. See FieldHashingOptions remarks for
        // the rotation discipline.
        services.AddOptions<FieldHashingOptions>()
            .Bind(configuration.GetSection(FieldHashingOptions.SectionName));
        var hashingSaltConfigured = !string.IsNullOrWhiteSpace(
            configuration[$"{FieldHashingOptions.SectionName}:SaltKey"]);
        if (hashingSaltConfigured)
        {
            services.AddSingleton<IDeterministicHasher, Hmac256Hasher>();
        }
        else
        {
            services.AddSingleton<IDeterministicHasher, MissingSaltHmacHasher>();
        }

        // R0025 — Connection-pool sizing for PSR 003 (2000 concurrent users) with
        // PgBouncer fronting Postgres in transaction-pooling mode. The pool defaults
        // (MaxPoolSize=2000, MinPoolSize=5, …) live on PostgresPoolOptions and are
        // bindable from Postgres:Pool:* so operators can override per environment
        // without redeploying the chart. See docs/operations.md §"Database connection
        // pooling (R0025)" and the type-level remarks on PostgresPoolOptions.
        services.AddOptions<PostgresPoolOptions>()
            .Bind(configuration.GetSection(PostgresPoolOptions.SectionName))
            .Validate(static o =>
                o.MaxPoolSize > 0
                && o.MinPoolSize >= 0
                && o.MinPoolSize <= o.MaxPoolSize
                && o.CommandTimeout > 0
                && o.ConnectionIdleLifetime > 0
                && o.ConnectionPruningInterval > 0,
                "Postgres:Pool values must be positive, and MinPoolSize must not exceed MaxPoolSize.")
            .ValidateOnStart();

        services.AddDbContext<CnasDbContext>((sp, opts) =>
        {
            var connectionString = configuration.GetConnectionString("Postgres")
                ?? throw new InvalidOperationException("Missing connection string 'Postgres'.");
            var pool = sp.GetRequiredService<IOptions<PostgresPoolOptions>>().Value;

            // Augment the operator-supplied connection string with the per-pod pool
            // sizing. We round-trip through NpgsqlConnectionStringBuilder so any
            // value-override the operator already wrote (e.g. for psql-style debugging
            // against a local Postgres) is preserved on every key we don't explicitly
            // set here.
            var builder = new NpgsqlConnectionStringBuilder(connectionString)
            {
                MaxPoolSize = pool.MaxPoolSize,
                MinPoolSize = pool.MinPoolSize,
                ConnectionIdleLifetime = pool.ConnectionIdleLifetime,
                ConnectionPruningInterval = pool.ConnectionPruningInterval,
                CommandTimeout = pool.CommandTimeout,
            };
            if (pool.UsePgBouncer)
            {
                // Transaction-pooled PgBouncer cannot support session-state prepared
                // statements (the bound statement may be dispatched to a different
                // backend on the next checkout). PgBouncer also performs its own
                // server-reset between transactions via SERVER_RESET_QUERY, so Npgsql's
                // built-in reset would be redundant — `EnableTypeLoading(false)` on the
                // data-source builder (set below via ConfigureDataSource) skips the
                // pg_catalog round-trip that PgBouncer cannot proxy reliably in
                // transaction mode. See PostgresPoolOptions type remarks.
                builder.MaxAutoPrepare = 0;
                builder.NoResetOnClose = true;
            }
            opts.UseNpgsql(builder.ConnectionString, npg =>
            {
                npg.EnableRetryOnFailure(5);
                npg.MigrationsHistoryTable("__EFMigrationsHistory", "cnas");
                if (pool.UsePgBouncer)
                {
                    // Npgsql 10 moved `Server Compatibility Mode = NoTypeLoading` off the
                    // connection string onto the DataSource builder. This path produces the
                    // same wire behaviour: pg_catalog type discovery is suppressed so the
                    // session-state-free PgBouncer transaction pool can serve us.
                    npg.ConfigureDataSource(static dsb =>
                        dsb.ConfigureTypeLoading(static tl => tl.EnableTypeLoading(false)));
                }
            });

            // R0184 / TOR SEC 042 — universal auto-audit SaveChanges hook. The
            // interceptor itself is registered Scoped via AddScoped above; resolving
            // it from the per-request service-provider snapshot here makes its
            // ICallerContext / IAuditService dependencies pick up the request-scoped
            // instances rather than the root-scope copies.
            var interceptor = sp.GetRequiredService<
                Cnas.Ps.Infrastructure.Persistence.Interceptors.AuditingInterceptor>();
            opts.AddInterceptors(interceptor);
            // R0191 / TOR SEC 050 / TOR ARH 028 — history-snapshot SaveChanges hook.
            // Scoped lifetime + per-request resolve mirrors the AuditingInterceptor
            // pattern so its ICnasTimeProvider + ICallerContext dependencies are the
            // request-scoped instances.
            var historyInterceptor = sp.GetRequiredService<
                Cnas.Ps.Infrastructure.Persistence.Interceptors.HistoryTrackingInterceptor>();
            opts.AddInterceptors(historyInterceptor);
        });
        services.AddScoped<ICnasDbContext>(sp => sp.GetRequiredService<CnasDbContext>());

        // R0026 — read-only context routed to the Postgres streaming-replication replica
        // (TOR PSR 006 / ARH 025). Reporting aggregations and Annex 5/6 long-running list
        // queries flow through this seam so the primary backend is not crushed by
        // analytical workloads. When ConnectionStrings:PostgresReadReplica is unset the
        // wiring transparently falls back to the primary and emits a WARN log line so
        // operators see the fallback (see ReadReplicaConfiguration). The Npgsql pool
        // sizing mirrors the primary so a single misconfigured pod still starts; a
        // future iteration may expose a separate Postgres:ReplicaPool section if the
        // analytical workload needs a different cap.
        services.AddDbContext<CnasReadOnlyDbContext>((sp, opts) =>
        {
            // Resolve the connection string here (rather than at the closure capture site
            // above) so the host's ILoggerFactory is the one wired to emit the WARN line —
            // resolving the factory eagerly at AddCnasInfrastructure time would build a
            // disposable factory that competes with the host's logging configuration.
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var replicaConnectionString = ReadReplicaConfiguration.ResolveConnectionString(
                configuration, loggerFactory);
            var pool = sp.GetRequiredService<IOptions<PostgresPoolOptions>>().Value;
            var builder = new NpgsqlConnectionStringBuilder(replicaConnectionString)
            {
                MaxPoolSize = pool.MaxPoolSize,
                MinPoolSize = pool.MinPoolSize,
                ConnectionIdleLifetime = pool.ConnectionIdleLifetime,
                ConnectionPruningInterval = pool.ConnectionPruningInterval,
                CommandTimeout = pool.CommandTimeout,
                ApplicationName = "cnas-ps-readonly",
            };
            if (pool.UsePgBouncer)
            {
                // Same PgBouncer transaction-mode constraints as the primary — see the
                // primary registration above for the full rationale.
                builder.MaxAutoPrepare = 0;
                builder.NoResetOnClose = true;
            }
            opts.UseNpgsql(builder.ConnectionString, npg =>
            {
                npg.EnableRetryOnFailure(5);
                npg.MigrationsHistoryTable("__EFMigrationsHistory", "cnas");
                if (pool.UsePgBouncer)
                {
                    npg.ConfigureDataSource(static dsb =>
                        dsb.ConfigureTypeLoading(static tl => tl.EnableTypeLoading(false)));
                }
            });
        });
        services.AddScoped<IReadOnlyCnasDbContext>(sp => sp.GetRequiredService<CnasReadOnlyDbContext>());

        services.AddOptions<MinioOptions>()
            .Bind(configuration.GetSection(MinioOptions.SectionName))
            .Validate(o => !string.IsNullOrWhiteSpace(o.Endpoint), "Minio:Endpoint is required.")
            .ValidateOnStart();

        // MinIO credentials follow the same source-of-truth discipline as the field
        // encryption key (CLAUDE.md §1.8 / TOR SEC 005): when AccessKey/SecretKey are
        // absent we register a fail-loud sentinel rather than letting MinioClient.Build()
        // throw at DI activation time. The sentinel mirrors MissingKeyFieldEncryptor —
        // construction succeeds (so unrelated controllers activate cleanly and unrelated
        // health checks still pass), but any actual storage call throws with a clear
        // "MinIO not configured" diagnostic. See MissingMinioFileStorage remarks for
        // the full rationale.
        var minioAccessKey = configuration[$"{MinioOptions.SectionName}:AccessKey"];
        var minioSecretKey = configuration[$"{MinioOptions.SectionName}:SecretKey"];
        var minioCredsConfigured = !string.IsNullOrWhiteSpace(minioAccessKey)
            && !string.IsNullOrWhiteSpace(minioSecretKey);
        if (minioCredsConfigured)
        {
            services.AddSingleton<IMinioClient>(sp =>
            {
                var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<MinioOptions>>().Value;
                return new MinioClient()
                    .WithEndpoint(opts.Endpoint)
                    .WithCredentials(opts.AccessKey, opts.SecretKey)
                    .WithSSL(opts.UseSsl)
                    .Build();
            });
            services.AddSingleton<IFileStorage, MinioFileStorage>();
        }
        else
        {
            services.AddSingleton<IFileStorage, MissingMinioFileStorage>();
        }

        services.AddOptions<MGovOptions>()
            .Bind(configuration.GetSection(MGovOptions.SectionName))
            .ValidateOnStart();

        // MGov resilience pipeline configuration. Bind via explicit Configure callback
        // because positional records (MGovClientResilience) don't always round-trip
        // through the configuration binder's dictionary support cleanly across providers
        // — mirroring the MTlsOptions pattern below for the same reason. The dictionary
        // remains case-insensitive so operators can write either "MSign" or "msign" in
        // settings without breaking the binding.
        services.Configure<MGovResilienceOptions>(opts =>
        {
            var section = configuration.GetSection(MGovResilienceOptions.SectionName);
            var enabledValue = section["Enabled"];
            // Re-build a typed snapshot via reflection-free explicit copy. The Enabled
            // and Clients init-only properties are populated below; MGovResilienceOptions
            // already defaults Enabled=true and Clients=empty, so we only override when
            // configuration is present.
            if (!string.IsNullOrWhiteSpace(enabledValue)
                && bool.TryParse(enabledValue, out var enabled))
            {
                // TODO(cleanup): swap MGovResilienceOptions.Enabled from `init` to
                // `set` so this reflection workaround can be removed. Out of scope
                // for the current composition/DI batch (the type lives under
                // Cnas.Ps.Infrastructure/MGov/ which is excluded). The reflection
                // call works at runtime — see the property metadata above — and the
                // composition is the only writer, so the encapsulation cost is bounded.
                typeof(MGovResilienceOptions)
                    .GetProperty(nameof(MGovResilienceOptions.Enabled))!
                    .SetValue(opts, enabled);
            }
            var clients = section.GetSection("Clients");
            foreach (var child in clients.GetChildren())
            {
                // Per-service overrides. Any unspecified field falls through to the
                // MGovClientResilience constructor default — see its XML doc for the
                // defaulting policy.
                var defaults = new MGovClientResilience();
                int Parse(string key, int fallback)
                    => int.TryParse(child[key], out var v) ? v : fallback;
                opts.Clients[child.Key] = new MGovClientResilience(
                    MaxRetries: Parse(nameof(MGovClientResilience.MaxRetries), defaults.MaxRetries),
                    BaseDelayMs: Parse(nameof(MGovClientResilience.BaseDelayMs), defaults.BaseDelayMs),
                    JitterMs: Parse(nameof(MGovClientResilience.JitterMs), defaults.JitterMs),
                    CircuitBreakerFailureThreshold: Parse(nameof(MGovClientResilience.CircuitBreakerFailureThreshold), defaults.CircuitBreakerFailureThreshold),
                    CircuitBreakerSamplingSeconds: Parse(nameof(MGovClientResilience.CircuitBreakerSamplingSeconds), defaults.CircuitBreakerSamplingSeconds),
                    CircuitBreakerBreakDurationSeconds: Parse(nameof(MGovClientResilience.CircuitBreakerBreakDurationSeconds), defaults.CircuitBreakerBreakDurationSeconds),
                    AttemptTimeoutSeconds: Parse(nameof(MGovClientResilience.AttemptTimeoutSeconds), defaults.AttemptTimeoutSeconds),
                    PipelineTimeoutSeconds: Parse(nameof(MGovClientResilience.PipelineTimeoutSeconds), defaults.PipelineTimeoutSeconds));
            }
        });

        // MSign uses mTLS per the real MEGA spec — the primary handler attaches the
        // certificate registered under Cnas:MGov:Mtls:Certificates:msign. The Bearer
        // header path is gone (MSignBearer is now [Obsolete]). If no certificate is
        // configured the handler is still SocketsHttpHandler (so a cert can be added
        // later without re-registration) but ClientCertificates is empty, falling back
        // to a Bearer-less HTTPS handshake — useful for dev/CI where MSign is mocked.
        services.AddHttpClient<IMSignClient, MSignClient>(nameof(MSignClient))
            .ConfigureHttpClient(ConfigureMGovHttp)
            .ConfigurePrimaryHttpMessageHandler(sp => BuildMGovPrimaryHandler(sp, "msign"))
            .AddMGovResilience("msign");
        // MPay uses mTLS per the real MEGA spec — the primary handler attaches the
        // certificate registered under Cnas:MGov:Mtls:Certificates:mpay. The Bearer
        // header path is gone (MPayBearer is now [Obsolete]). When no certificate is
        // configured the handler still uses SocketsHttpHandler (so a cert can be added
        // without re-registration) but with an empty ClientCertificates list, falling
        // back to a Bearer-less HTTPS handshake — used by dev/CI where MPay is mocked.
        services.AddHttpClient<IMPayClient, MPayClient>(nameof(MPayClient))
            .ConfigureHttpClient(ConfigureMGovHttp)
            .ConfigurePrimaryHttpMessageHandler(sp => BuildMGovPrimaryHandler(sp, "mpay"))
            .AddMGovResilience("mpay");
        // MConnect uses mTLS per the real MEGA spec — the primary handler attaches the
        // certificate registered under Cnas:MGov:Mtls:Certificates:mconnect. The Bearer
        // header path is gone (MConnectBearer is now [Obsolete]). When no certificate is
        // configured the handler still uses SocketsHttpHandler (so a cert can be added
        // without re-registration) but with an empty ClientCertificates list, falling
        // back to a Bearer-less HTTPS handshake — used by dev/CI where MConnect is mocked.
        services.AddHttpClient<IMConnectClient, MConnectClient>(nameof(MConnectClient))
            .ConfigureHttpClient(ConfigureMGovHttp)
            .ConfigurePrimaryHttpMessageHandler(sp => BuildMGovPrimaryHandler(sp, "mconnect"))
            .AddMGovResilience("mconnect");
        // MNotify uses mTLS per the real MEGA spec — the primary handler attaches the
        // certificate loaded from ICertificateStore.TryGetCertificate("mnotify"). If no
        // certificate is configured the handler is still SocketsHttpHandler (so a future
        // cert can be attached without a registration change) but ClientCertificates is
        // empty, falling back to a Bearer-less HTTPS handshake — useful for dev/CI.
        services.AddHttpClient<IMNotifyClient, MNotifyClient>(nameof(MNotifyClient))
            .ConfigureHttpClient(ConfigureMGovHttp)
            .ConfigurePrimaryHttpMessageHandler(sp => BuildMGovPrimaryHandler(sp, "mnotify"))
            .AddMGovResilience("mnotify");
        // MLog also uses mTLS (cert fingerprint authentication per the MEGA spec); the
        // primary handler factory mirrors the MNotify path. The legacy AppendAsync shim
        // continues to work through the same wire because both endpoints share the
        // primary handler.
        services.AddHttpClient<IMLogClient, MLogClient>(nameof(MLogClient))
            .ConfigureHttpClient(ConfigureMGovHttp)
            .ConfigurePrimaryHttpMessageHandler(sp => BuildMGovPrimaryHandler(sp, "mlog"))
            .AddMGovResilience("mlog");
        // NOTE: IMPowerClient HTTP adapter is intentionally removed — MPower is consumed
        // indirectly via MPass SAML claims. See docs/EGOV-INTEGRATION-GAP.md §"MPower".

        // MDocs uses a longer timeout (60s) than the rest of MGov because document
        // uploads can be many megabytes. The User-Agent matches so AGE-side audit
        // dashboards continue to attribute the traffic to CNAS-PS. Like the rest of the
        // MGov clients, MDocs uses mTLS — the primary handler attaches the certificate
        // registered under Cnas:MGov:Mtls:Certificates:mdocs (Bearer-less HTTPS fallback
        // applies when no certificate is configured, used by dev/CI).
        services.AddHttpClient<IMDocsClient, MDocsClient>(nameof(MDocsClient))
            .ConfigureHttpClient((sp, c) =>
            {
                c.Timeout = TimeSpan.FromSeconds(60);
                c.DefaultRequestHeaders.UserAgent.ParseAdd("CNAS-PS/1.0");
            })
            .ConfigurePrimaryHttpMessageHandler(sp => BuildMGovPrimaryHandler(sp, "mdocs"))
            .AddMGovResilience("mdocs");

        // MConnect Events producer — CloudEvents v1.0 over HTTP. The consumer below is a
        // long-running BackgroundService that holds a WebSocket open to MConnect Events
        // and dispatches received CloudEvents to ICloudEventHandler instances. The
        // LoggingCloudEventHandler is the default catch-all; domain-specific handlers
        // (RSP citizen-updated, SFS payment-confirmed, ...) are added by feature epics.
        // The producer uses mTLS via the shared primary-handler factory — the certificate
        // is registered under Cnas:MGov:Mtls:Certificates:mconnect-events. The consumer's
        // WebSocket runs in its own BackgroundService and is not wired through this typed
        // HttpClient registration.
        services.AddHttpClient<IMConnectEventsProducer, MConnectEventsProducer>(nameof(MConnectEventsProducer))
            .ConfigureHttpClient(ConfigureMGovHttp)
            .ConfigurePrimaryHttpMessageHandler(sp => BuildMGovPrimaryHandler(sp, "mconnect-events"))
            .AddMGovResilience("mconnect-events");
        // R0103 — inbound integration-event deduper. Backed by the
        // ProcessedIntegrationEvents table; consulted by
        // LoggingCloudEventHandler as the first action on every inbound
        // CloudEvent. Scoped because it touches the per-request DbContext.
        services.AddScoped<Cnas.Ps.Application.MessageBus.IIntegrationEventDeduper,
            Cnas.Ps.Infrastructure.Services.MessageBus.IntegrationEventDeduper>();

        // LoggingCloudEventHandler is Scoped (not Singleton) because it now
        // depends on the per-request IIntegrationEventDeduper. The consumer
        // creates a fresh DI scope per received frame so this is safe.
        services.AddScoped<ICloudEventHandler, LoggingCloudEventHandler>();
        services.AddHostedService<MConnectEventsConsumer>();

        // Typed facades over MConnect (one per external system listed in TOR §2.1).
        // Each facade depends on IMConnectClient and is scoped because the underlying
        // HttpClient lifetime is owned by IHttpClientFactory and MConnect itself is registered
        // as a typed HttpClient. See src/Cnas.Ps.Infrastructure/MGov/External/.
        services.AddScoped<IRspClient, RspClient>();
        services.AddScoped<IRsudClient, RsudClient>();
        services.AddScoped<ISfsClient, SfsClient>();
        services.AddScoped<ISiddcmClient, SiddcmClient>();
        services.AddScoped<IPccmClient, PccmClient>();
        services.AddScoped<IECmndClient, ECmndClient>();
        services.AddScoped<ISiaIssClient, SiaIssClient>();
        services.AddScoped<ISiveClient, SiveClient>();
        services.AddScoped<ISiaasClient, SiaasClient>();
        services.AddScoped<IFmsClient, FmsClient>();
        services.AddScoped<IEessiClient, EessiClient>();

        // R0186 — async batched audit pipeline. The queue is a singleton (one channel
        // per process, shared by every scoped AuditService instance and the singleton
        // drainer); the drainer is a long-running BackgroundService that opens its own
        // scope per flush to resolve the scoped DbContext + MLog client.
        // R0188 — durable audit archive + replay. The archive is a singleton (stateless
        // beyond its configured root directory) and is consulted by the drainer on flush
        // failure so batches survive transient DB / MLog outages. The Quartz replay job
        // (registered via AddCnasJobs) wakes every 5 minutes to retry spilled batches.
        services.AddOptions<AuditArchiveOptions>()
            .Bind(configuration.GetSection(AuditArchiveOptions.SectionName));
        services.AddSingleton<IAuditArchive, LocalDiskAuditArchive>();
        services.AddSingleton<AuditWriteQueue>();
        services.AddHostedService<AuditDrainer>();
        services.AddScoped<IAuditService, AuditService>();

        // R0190 / SEC 049 — SIEM CEF / syslog forwarder. The exporter is stateless and
        // thread-safe (one UdpClient per ForwardAsync call) so it's registered as a
        // singleton. Disabled by default — operators flip SiemExporterOptions.Enabled
        // to opt in once their SIEM endpoint is provisioned. The Quartz polling job is
        // registered via AddCnasJobs alongside the rest of the job set.
        services.AddOptions<SiemExporterOptions>()
            .Bind(configuration.GetSection(SiemExporterOptions.SectionName));
        services.AddSingleton<ISiemExporter, SyslogCefSiemExporter>();

        // R0189 / SEC 048 — Security-alert evaluator. The job itself is stateless
        // (re-resolves scoped DbContext + IAuditService + INotificationService per
        // fire); options bind from Cnas:SecurityAlerts. Enabled by default because
        // the migration seeds the common rules — operators may flip Enabled=false
        // to delegate alerting to an external rule engine. The Quartz trigger is
        // registered via AddCnasJobs.
        services.AddOptions<SecurityAlertOptions>()
            .Bind(configuration.GetSection(SecurityAlertOptions.SectionName));

        // R2173 / TOR PSR 004 — peak-hour gate. Singleton because the gate is
        // stateless beyond its options snapshot and the gate is called from
        // every job's Execute; a singleton avoids per-fire allocation. Options
        // bind from Cnas:PeakHourGate (defaults: 22..06 off-peak, override=false).
        // The override store is a separate singleton seeded from the option
        // default and mutated by the admin controller at runtime — the
        // gate consults both surfaces. The gate resolves IAuditService via a
        // scope factory so the audit-write path stays scope-bound on Skip emissions.
        services.AddOptions<Cnas.Ps.Infrastructure.Scheduling.PeakHourGateOptions>()
            .Bind(configuration.GetSection(Cnas.Ps.Infrastructure.Scheduling.PeakHourGateOptions.SectionName));
        services.AddSingleton<Cnas.Ps.Infrastructure.Scheduling.PeakHourGateOverrideStore>(sp =>
        {
            var seed = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<
                Cnas.Ps.Infrastructure.Scheduling.PeakHourGateOptions>>().Value.GlobalOverride;
            return new Cnas.Ps.Infrastructure.Scheduling.PeakHourGateOverrideStore(seed);
        });
        services.AddSingleton<Cnas.Ps.Application.Scheduling.IPeakHourGate,
            Cnas.Ps.Infrastructure.Scheduling.PeakHourGate>();

        // R0040 — custom OTel metrics surface. The backlog observer caches the
        // pending-admin-action count on a 30-second cadence so the static-meter
        // gauge callback is non-blocking; the metrics initializer registers all
        // observable gauges at process start (after DI is fully built). The
        // initializer's StartAsync runs once and is a no-op on shutdown — the
        // meter remains alive so process-end exports flush cleanly.
        services.AddSingleton<AdminActionBacklogObserver>();
        services.AddHostedService(sp => sp.GetRequiredService<AdminActionBacklogObserver>());
        services.AddHostedService<CnasMetricsInitializer>();

        // R0194 / SEC 047 — hash-chain verifier. Scoped because the underlying
        // IReadOnlyCnasDbContext is scoped (per-request streaming-replica
        // routing). Pure read; never mutates the chain.
        services.AddScoped<IAuditChainVerifier, AuditChainVerifier>();
        // R0172 / TOR CF 22.05 — deep-link resolver consumed by
        // NotificationService.InboxAsync. Stateless + thread-safe so singleton
        // is the right lifetime (mirrors ISqidService which it depends on).
        services.AddSingleton<
            Cnas.Ps.Application.Notifications.INotificationDeepLinkResolver,
            Cnas.Ps.Infrastructure.Notifications.NotificationDeepLinkResolver>();
        services.AddScoped<INotificationService, NotificationService>();
        // R0174 / TOR CF 22.03 — central five-trigger dispatcher consumed by
        // WorkflowTaskService / DossierSlaMonitorJob / DecisionWorkflowService /
        // ReportJobBackgroundJob. Scoped — wraps the per-request INotificationService.
        services.AddScoped<
            Cnas.Ps.Application.Notifications.INotificationTriggerDispatcher,
            Cnas.Ps.Infrastructure.Notifications.NotificationTriggerDispatcher>();
        // R0174 / TOR CF 22.03 — perf-alert sweep options (off-peak default 5 minutes).
        services.AddOptions<Cnas.Ps.Infrastructure.Jobs.ReportJobOverrunOptions>()
            .BindConfiguration("Cnas:ReportJobs");
        services.AddScoped<Cnas.Ps.Infrastructure.Jobs.ReportJobOverrunMonitorJob>();
        services.AddScoped<IInformationServices, InformationServices>();
        services.AddScoped<IPublicContentService, PublicContentService>();
        services.AddScoped<IDataSearchService, DataSearchService>();
        // R0160 / R0161 / TOR CF 03.03 — cross-domain Postgres FTS surface.
        // Scoped because it consumes the per-request IReadOnlyCnasDbContext
        // (streaming-replica routing per R0026 / ARH 025).
        services.AddScoped<
            Cnas.Ps.Application.Search.IGlobalSearchService,
            Cnas.Ps.Infrastructure.Search.PostgresGlobalSearchService>();
        // R0501 / TOR CF 01.04 — metadata-driven search-criteria catalogue.
        // Stateless and thread-safe — singleton lifetime. Lives in the
        // Application layer because the descriptor table has no infrastructure
        // dependencies (Contracts only).
        services.AddSingleton<
            Cnas.Ps.Application.Search.ISearchCriteriaCatalog,
            Cnas.Ps.Application.Search.StaticSearchCriteriaCatalog>();
        // R0526 / TOR CF 03.10 — row-level scope filter consulted by every
        // per-domain projector inside the unified search service. Scoped
        // because it consumes IReadOnlyCnasDbContext.
        services.AddScoped<
            Cnas.Ps.Application.Search.ISearchRowLevelFilter,
            Cnas.Ps.Infrastructure.Search.AbacSearchRowLevelFilter>();
        // R0520 / TOR CF 03.01 — unified cross-entity search service. Scoped
        // because it consumes the per-request IReadOnlyCnasDbContext.
        services.AddScoped<
            Cnas.Ps.Application.Search.IUnifiedDataSearchService,
            Cnas.Ps.Infrastructure.Search.UnifiedDataSearchService>();
        // R0530 / R0531 / CF 04.01-04.02 — dashboard registry + per-category tile
        // producers. Registry is Singleton (stateless lookup table); producers are
        // Scoped because each captures the per-request read DbContext.
        services.AddSingleton(Cnas.Ps.Application.Dashboard.DashboardWidgetRegistry.Default);
        services.AddScoped<
            Cnas.Ps.Application.Dashboard.IDashboardTileProducer,
            Cnas.Ps.Infrastructure.Services.Dashboard.WorkflowUpdatesTileProducer>();
        services.AddScoped<
            Cnas.Ps.Application.Dashboard.IDashboardTileProducer,
            Cnas.Ps.Infrastructure.Services.Dashboard.InvolvementTileProducer>();
        services.AddScoped<
            Cnas.Ps.Application.Dashboard.IDashboardTileProducer,
            Cnas.Ps.Infrastructure.Services.Dashboard.AwaitingApprovalTileProducer>();
        // R0170 / TOR CF 22.02 + CF 04.02 — unread-notifications tile producer.
        services.AddScoped<
            Cnas.Ps.Application.Dashboard.IDashboardTileProducer,
            Cnas.Ps.Infrastructure.Services.Dashboard.UnreadNotificationsTileProducer>();
        // R0536 / TOR CF 04.09 (iter 134) — Solicitant-scoped KPI producers. Each
        // reads the replica via IReadOnlyCnasDbContext and scopes to the calling
        // user's Solicitant id; the completed-in-window producer also captures the
        // clock so the 30-day rolling window is deterministic under test harness.
        services.AddScoped<
            Cnas.Ps.Application.Dashboard.IDashboardTileProducer,
            Cnas.Ps.Infrastructure.Services.Dashboard.MyRequestsInExaminationKpiProducer>();
        services.AddScoped<
            Cnas.Ps.Application.Dashboard.IDashboardTileProducer,
            Cnas.Ps.Infrastructure.Services.Dashboard.MyRequestsCompletedInWindowKpiProducer>();
        services.AddScoped<
            Cnas.Ps.Application.Dashboard.IDashboardTileProducer,
            Cnas.Ps.Infrastructure.Services.Dashboard.MyRequestsByStatusKpiProducer>();
        // R0533 / TOR CF 04.04 — aggregate KPI grid producers. Scoped because each
        // captures the per-request read DbContext + (where needed) the clock.
        services.AddScoped<
            Cnas.Ps.Application.Dashboard.IKpiGridProducer,
            Cnas.Ps.Infrastructure.Services.Dashboard.UnreadNotificationsKpiGridProducer>();
        services.AddScoped<
            Cnas.Ps.Application.Dashboard.IKpiGridProducer,
            Cnas.Ps.Infrastructure.Services.Dashboard.DocsPendingApprovalKpiGridProducer>();
        services.AddScoped<
            Cnas.Ps.Application.Dashboard.IKpiGridProducer,
            Cnas.Ps.Infrastructure.Services.Dashboard.ApplicationsByStatusKpiGridProducer>();
        services.AddScoped<
            Cnas.Ps.Application.Dashboard.IKpiGridProducer,
            Cnas.Ps.Infrastructure.Services.Dashboard.OverdueTasksKpiGridProducer>();
        services.AddScoped<IDashboardService, DashboardService>();
        services.AddScoped<ITaskInboxService, TaskInboxService>();
        // R0127 / CF 16.11 — user-absence orchestration. Scoped because the service
        // captures the per-request DbContext + caller context for audit attribution.
        services.AddScoped<IUserAbsenceService, UserAbsenceService>();
        // R0570 / TOR CF 08.02 — round-robin examiner assignment service.
        // Scoped because it captures the per-request DbContext + clock for
        // the singleton-row cursor read-modify-write.
        services.AddScoped<IExaminerAssignmentService,
            Cnas.Ps.Infrastructure.Services.ApplicationProcessing.RoundRobinExaminerAssignmentService>();

        // R0540 / TOR CF 05.01 (iter 134) — rule-driven workflow-task auto-creator.
        // BPM-engine-independent default; once R0120 (Operaton) lands a second
        // implementation can be slotted at this seam. Scoped because the
        // implementation captures the per-request DbContext + clock.
        services.AddScoped<
            Cnas.Ps.Application.Workflow.IWorkflowTaskAutoCreator,
            RuleDrivenWorkflowTaskAutoCreator>();

        services.AddScoped<IApplicationService, ApplicationServiceImpl>();

        // R0939 / iter 136 — centralised application-status guard. Reads the current
        // ApplicationStatus from the replica and consults the pinned 8-state
        // transition matrix (Cnas.Ps.Core.ValueObjects.ApplicationStatusTransitions).
        // Scoped because the implementation captures the per-request read-only
        // DbContext.
        services.AddScoped<IApplicationStatusGuard, ApplicationStatusGuard>();

        services.AddScoped<IContributorService, ContributorService>();
        services.AddScoped<IInsuredPersonService, InsuredPersonService>();
        // R0301 / R0311 — Payer + Contributor linked-entity (address / contact / activity /
        // history) services. Scoped because each captures the per-request DbContext + caller
        // context for audit attribution.
        services.AddScoped<
            Cnas.Ps.Application.Payers.IPayerLinkedEntitiesService,
            Cnas.Ps.Infrastructure.Services.PayerLinkedEntitiesService>();
        services.AddScoped<
            Cnas.Ps.Application.Contributors.IContributorLinkedEntitiesService,
            Cnas.Ps.Infrastructure.Services.ContributorLinkedEntitiesService>();

        // R0362 / R0363 — profile-update + external-refresh services (UC13 strategies 2/3).
        // The gateways are scoped so they share the per-request MConnect typed client
        // lifetime when real wiring lands; today they resolve to mock gateways that
        // emit hand-coded deltas until the NDA-gated WSDLs are obtained.
        services.AddScoped<Cnas.Ps.Application.External.IRspGateway,
            Cnas.Ps.Infrastructure.MGov.MockRspGateway>();
        services.AddScoped<Cnas.Ps.Application.External.IRsudGateway,
            Cnas.Ps.Infrastructure.MGov.MockRsudGateway>();
        services.AddScoped<Cnas.Ps.Application.External.ISiSfsGateway,
            Cnas.Ps.Infrastructure.MGov.MockSiSfsGateway>();
        services.AddOptions<Cnas.Ps.Infrastructure.Services.ProfileRefreshOptions>()
            .Bind(configuration.GetSection(Cnas.Ps.Infrastructure.Services.ProfileRefreshOptions.SectionName));
        services.AddScoped<Cnas.Ps.Application.ContributorProfileUpdates.IProfileUpdateService,
            Cnas.Ps.Infrastructure.Services.ProfileUpdateService>();
        services.AddScoped<Cnas.Ps.Application.ContributorProfileUpdates.IProfileRefreshService,
            Cnas.Ps.Infrastructure.Services.ProfileRefreshService>();
        // ProfileRefreshScheduledJob is deliberately NOT registered with Quartz here —
        // see ProfileRefreshOptions.EnableScheduledRefresh for the gating rationale. The
        // class is wired into DI so it can be resolved by tests, but Quartz never picks
        // it up until the integration ships.
        services.AddTransient<Cnas.Ps.Infrastructure.Jobs.ProfileRefreshScheduledJob>();

        // R0552 / R0562 — pre-fill service (TOR CF 06.03 + CF 07.03). Reuses the three
        // R0363 gateway registrations above (they double-implement IPrefillSourceAdapter
        // so the same singletons answer both refresh and pre-fill calls). Scoped because
        // the implementation depends on the per-request caller context + audit sink.
        services.AddScoped<Cnas.Ps.Application.Prefill.IPrefillService,
            Cnas.Ps.Infrastructure.Services.Prefill.PrefillService>();

        // R0623 / TOR CF 13.04 — Solicitant reference-blocking guard. Scoped
        // because it consumes the per-request IReadOnlyCnasDbContext (R0026 /
        // ARH 025 read-replica routing). Consumed by SolicitantService.DeactivateAsync.
        services.AddScoped<Cnas.Ps.Application.Solicitants.ISolicitantReferenceGuard,
            Cnas.Ps.Infrastructure.Services.Solicitants.SolicitantReferenceGuard>();

        // R0672 / TOR CF 18.08 — user-deactivation audit-history guard.
        // Scoped because it consumes the per-request IReadOnlyCnasDbContext.
        // Consumed by UserAdministrationService.DeactivateAsync.
        services.AddScoped<Cnas.Ps.Application.Users.IUserDeactivationGuard,
            Cnas.Ps.Infrastructure.Services.Users.UserDeactivationGuard>();

        // R0673 / TOR CF 18.12 — granular permission matrix service.
        // Scoped because it consumes the per-request ICnasDbContext +
        // IReadOnlyCnasDbContext + ICallerContext. Backs the
        // /api/admin/permissions REST surface and the [GranularPermission]
        // action filter that gates select controller actions.
        services.AddScoped<Cnas.Ps.Application.Permissions.IGranularPermissionService,
            Cnas.Ps.Infrastructure.Services.Permissions.GranularPermissionService>();

        // R0580 / TOR CF 09.02 — ad-hoc report builder. Scoped because it
        // consumes the per-request IReadOnlyCnasDbContext.
        services.AddScoped<Cnas.Ps.Application.Reports.IAdHocReportBuilder,
            Cnas.Ps.Infrastructure.Services.Reports.LinqAdHocReportBuilder>();

        // R0602 / TOR CF 11.03 — paper-channel fulfilment workflow service.
        // Scoped because it consumes the per-request ICnasDbContext +
        // ICnasTimeProvider + ICallerContext + IAuditService.
        services.AddScoped<Cnas.Ps.Application.Documents.IPaperFulfilmentService,
            Cnas.Ps.Infrastructure.Services.Documents.PaperFulfilmentService>();

        services.AddScoped<ISolicitantService, SolicitantService>();

        // R0671 / TOR CF 18.06 — row-level access-scope filter + descriptor service.
        // The filter is stateless (singleton); the descriptor service depends on the
        // per-request ICallerContext so it is scoped. The filter is consumed by
        // SolicitantService (and, in follow-up batches, by every list-style service);
        // the descriptor service backs the GET /api/profile/access-scope endpoint.
        services.AddSingleton<
            Cnas.Ps.Application.AccessScope.IAccessScopeFilter,
            Cnas.Ps.Infrastructure.AccessScope.AccessScopeFilter>();
        services.AddScoped<
            Cnas.Ps.Application.AccessScope.IAccessScopeService,
            Cnas.Ps.Infrastructure.AccessScope.AccessScopeService>();
        // R0671 continuation — admin back-fill helper for the access-scope columns.
        // Scoped because it depends on the per-request ICnasDbContext + ICallerContext.
        services.AddScoped<
            Cnas.Ps.Application.AccessScope.IAccessScopeBackfillService,
            Cnas.Ps.Infrastructure.AccessScope.AccessScopeBackfillService>();

        // R0502 / R0504 / R0505 / CF 01.05 / CF 01.06 / CF 01.08 — public
        // services-catalog read façade. Scoped so the per-request LastBudgetVerdict
        // slot remains isolated per HTTP request (mirrors SolicitantService).
        services.AddScoped<
            Cnas.Ps.Application.PublicCatalog.IPublicCatalogService,
            Cnas.Ps.Infrastructure.Services.PublicCatalogService>();
        services.AddScoped<
            FluentValidation.IValidator<Cnas.Ps.Contracts.PublicCatalogListQueryDto>,
            Cnas.Ps.Application.Validators.PublicCatalogListQueryValidator>();

        // R0511 / R0512 / R0513 / CF 02.01 — public anonymous services
        // (medical-certificate status, online-appointment booking, extract CNAS
        // code). All three are scoped because they own per-request audit /
        // caller context. The PCCM gateway is stubbed to MockPccmGateway until
        // the real SOAP/REST integration ships (deferred); appointment options
        // bind from Cnas:PublicServices:Appointments.
        services.AddOptions<Cnas.Ps.Infrastructure.Services.PublicServices.AppointmentBookingOptions>()
            .Bind(configuration.GetSection(
                Cnas.Ps.Infrastructure.Services.PublicServices.AppointmentBookingOptions.SectionName));
        services.AddScoped<
            Cnas.Ps.Application.PublicServices.IPccmGateway,
            Cnas.Ps.Infrastructure.Services.PublicServices.MockPccmGateway>();
        services.AddScoped<
            Cnas.Ps.Application.PublicServices.IMedicalCertificateStatusService,
            Cnas.Ps.Infrastructure.Services.PublicServices.MedicalCertificateStatusService>();
        services.AddScoped<
            Cnas.Ps.Application.PublicServices.IOnlineAppointmentBookingService,
            Cnas.Ps.Infrastructure.Services.PublicServices.OnlineAppointmentBookingService>();
        services.AddScoped<
            Cnas.Ps.Application.PublicServices.IExtractCnasCodeService,
            Cnas.Ps.Infrastructure.Services.PublicServices.ExtractCnasCodeService>();

        // R0514 / CF 02.02 — citizen pension-projection simulator.
        // R0516 / CF 02.04 — citizen personal-account extract.
        // Both are authenticated self-service surfaces. The pension calculator
        // is stateless but scoped because it carries the per-request audit /
        // caller context; the personal-account extract is scoped for the same
        // reason and because it holds an EF DbContext.
        services.AddOptions<Cnas.Ps.Infrastructure.Services.PensionOptions>()
            .Bind(configuration.GetSection(
                Cnas.Ps.Infrastructure.Services.PensionOptions.SectionName));
        services.AddScoped<
            Cnas.Ps.Application.Pension.IPensionCalculatorService,
            Cnas.Ps.Infrastructure.Services.PensionCalculatorService>();
        services.AddScoped<
            Cnas.Ps.Application.PersonalAccount.IPersonalAccountExtractService,
            Cnas.Ps.Infrastructure.Services.PersonalAccountExtractService>();

        // R0517 / TOR CF 02.05 — citizen "status of benefit payments" service.
        // Scoped for the same lifetime reason as the personal-account extract:
        // the implementation depends on the per-request caller context and an
        // EF DbContext (read-side via IReadOnlyCnasDbContext + write-side for
        // the UserProfile→Solicitant identity link).
        services.AddScoped<
            Cnas.Ps.Application.Benefits.IBenefitPaymentStatusService,
            Cnas.Ps.Infrastructure.Services.Benefits.BenefitPaymentStatusService>();

        // R0810 / R0811 / R0812 / R0813 — declarations registry (BP 1.2 A/B/C) +
        // monthly aggregator (BP 1.2-D). Both services are scoped because they
        // depend on the per-request DbContext + caller context for audit
        // attribution. The aggregator reads via IReadOnlyCnasDbContext per the
        // R0026 routing convention; the writer uses ICnasDbContext.
        services.AddScoped<
            Cnas.Ps.Application.Declarations.IDeclarationService,
            Cnas.Ps.Infrastructure.Services.Declarations.DeclarationService>();
        services.AddScoped<
            Cnas.Ps.Application.Declarations.IMonthlyContributionCalculator,
            Cnas.Ps.Infrastructure.Services.Declarations.MonthlyContributionCalculator>();

        // R0819 / BP 1.2-J — late-payment-penalty calculator.
        // R0820 / BP 1.2-K — management-period close / re-open service.
        // Both scoped because the implementations depend on the per-request
        // DbContext + caller context for audit attribution. Penalty options
        // bound from the Cnas:Penalty configuration section with sensible
        // defaults baked in.
        services.AddOptions<Cnas.Ps.Infrastructure.Services.Penalties.PenaltyOptions>()
            .Bind(configuration.GetSection(
                Cnas.Ps.Infrastructure.Services.Penalties.PenaltyOptions.SectionName));
        services.AddScoped<
            Cnas.Ps.Application.Penalties.ILatePaymentPenaltyCalculator,
            Cnas.Ps.Infrastructure.Services.Penalties.LatePaymentPenaltyCalculator>();
        services.AddScoped<
            Cnas.Ps.Application.ManagementPeriods.IManagementPeriodService,
            Cnas.Ps.Infrastructure.Services.Penalties.ManagementPeriodService>();

        // R0920 / R0921 / BP 2.3-A & B — labor-booklet (Carnet de muncă)
        // registry + pre-01.01.1999 activity periods. Scoped because the
        // implementation depends on the per-request DbContext + caller context
        // for audit attribution.
        services.AddScoped<
            Cnas.Ps.Application.LaborBooklet.ILaborBookletService,
            Cnas.Ps.Infrastructure.Services.LaborBooklet.LaborBookletService>();

        // R0922 / TOR Annex 2 §8.2.4 — InsuredPerson pre-1999 stagiu roll-up
        // service. Scoped because the implementation depends on the per-request
        // DbContext + caller context + audit/clock.
        services.AddScoped<
            Cnas.Ps.Application.LaborBooklet.IPre1999StagiuService,
            Cnas.Ps.Infrastructure.Services.LaborBooklet.Pre1999StagiuService>();

        // R0910 / BP 2.2-A — REV-5 declarations registry (per-employee
        // breakdown). R0913 / BP 2.2-D — per-insured-person contribution
        // adjustments from non-REV-5 supporting documents. Both services are
        // scoped because they depend on the per-request DbContext + caller
        // context for audit attribution.
        services.AddScoped<
            Cnas.Ps.Application.Rev5.IRev5DeclarationService,
            Cnas.Ps.Infrastructure.Services.Rev5.Rev5DeclarationService>();
        services.AddScoped<
            Cnas.Ps.Application.Rev5.IInsuredPersonAdjustmentService,
            Cnas.Ps.Infrastructure.Services.Rev5.InsuredPersonAdjustmentService>();

        // R0634 / TOR CF 14.12 / Annex 4 — B2B interop façade (RSP, MoFin,
        // IPS, SIVE, ...). Scoped because the implementation depends on the
        // per-request caller context (sourceIp / correlation id on audit
        // rows) and an EF DbContext. Read-only data path via
        // IReadOnlyCnasDbContext per TOR ARH 025.
        services.AddScoped<
            Cnas.Ps.Application.Interop.IInteropApi,
            Cnas.Ps.Infrastructure.Services.Interop.InteropService>();

        // R0167 / CF 01.06 / CF 03.07-08 — query-budget guard. The static policy
        // resolver is a singleton (immutable seed table); the budget service is
        // scoped to match the per-request lifetime of the consumers, though it is
        // stateless and could equally well be a singleton.
        services.AddSingleton<Cnas.Ps.Application.QueryBudget.IQueryBudgetPolicy,
            Cnas.Ps.Infrastructure.QueryBudget.StaticQueryBudgetPolicy>();
        services.AddScoped<Cnas.Ps.Application.QueryBudget.IQueryBudgetService,
            Cnas.Ps.Infrastructure.QueryBudget.QueryBudgetService>();

        // R0163 / TOR UI 009 — Query-By-Example primitive. The schema provider is a
        // singleton (immutable frozen seed); the converter is also singleton because it
        // is stateless (the per-call schema lookup goes through the singleton provider).
        services.AddSingleton<Cnas.Ps.Application.Qbe.IQbeRegistrySchemaProvider,
            Cnas.Ps.Infrastructure.Qbe.QbeRegistrySchemaProvider>();
        services.AddSingleton<Cnas.Ps.Application.Qbe.IQbeToLinqConverter,
            Cnas.Ps.Infrastructure.Qbe.QbeToLinqConverter>();

        // R0525 / TOR CF 03.08 — search-suggestion heuristic (stateless, schema-driven).
        // Singleton because the suggestion catalogue + threshold are invariant after
        // startup; the per-call work is pure.
        services.AddSingleton<Cnas.Ps.Application.Search.ISearchSuggestionService,
            Cnas.Ps.Infrastructure.Search.SearchSuggestionService>();

        // R0522 / TOR CF 03.03 — full-text search engine abstraction. The default
        // adapter uses Postgres ILIKE + diacritic folding (matches the bespoke
        // SolicitantService search behaviour). The placeholder
        // NotImplementedExternalSearchEngine documents the path to a real
        // Elasticsearch / Solr deployment (separate infra batch); it is NOT
        // registered here so a misconfigured wiring cannot silently degrade —
        // tests instantiate it directly.
        services.AddSingleton<Cnas.Ps.Application.Search.IFullTextSearchEngine,
            Cnas.Ps.Infrastructure.Search.PostgresIlikeFullTextSearchEngine>();

        // R0226 / TOR UI 013 — universal grid export. Renderers are stateless
        // singletons (one per format); the exporter façade is a singleton too
        // (no per-request state). The per-registry orchestrator is scoped so
        // the LastBudgetVerdict slot stays request-isolated.
        services.AddSingleton<Cnas.Ps.Application.Exports.IGridExportRenderer,
            Cnas.Ps.Infrastructure.Exports.CsvGridExportRenderer>();
        services.AddSingleton<Cnas.Ps.Application.Exports.IGridExportRenderer,
            Cnas.Ps.Infrastructure.Exports.XlsxGridExportRenderer>();
        services.AddSingleton<Cnas.Ps.Application.Exports.IGridExportRenderer,
            Cnas.Ps.Infrastructure.Exports.PdfGridExportRenderer>();
        services.AddSingleton<Cnas.Ps.Application.Exports.IGridExporter,
            Cnas.Ps.Infrastructure.Exports.GridExporter>();
        services.AddSingleton<Cnas.Ps.Application.Exports.SolicitantGridAdapter>();

        // R0529 / TOR CF 03.14 — universal report-export pipeline. Each exporter is
        // a stateless pure function on the input matrix (one Scoped instance per
        // request; the selector indexes them by format and dispatches in O(1)).
        services.AddScoped<Cnas.Ps.Application.Reporting.IReportExporter,
            Cnas.Ps.Infrastructure.Services.Reporting.CsvReportExporter>();
        services.AddScoped<Cnas.Ps.Application.Reporting.IReportExporter,
            Cnas.Ps.Infrastructure.Services.Reporting.XlsxReportExporter>();
        services.AddScoped<Cnas.Ps.Application.Reporting.IReportExporter,
            Cnas.Ps.Infrastructure.Services.Reporting.DocxReportExporter>();
        services.AddScoped<Cnas.Ps.Application.Reporting.IReportExporter,
            Cnas.Ps.Infrastructure.Services.Reporting.PdfReportExporter>();
        services.AddScoped<Cnas.Ps.Application.Reporting.IReportExportSelector,
            Cnas.Ps.Infrastructure.Services.Reporting.ReportExportSelector>();
        services.AddScoped<Cnas.Ps.Application.Exports.ISolicitantGridExportService,
            Cnas.Ps.Infrastructure.Exports.SolicitantGridExportService>();
        // R0193 / SEC 052 — admin audit explorer. Scoped because the underlying
        // services (IReadOnlyCnasDbContext + ICnasDbContext + IAuditService) are
        // scoped; the LastBudgetVerdict slot is implicitly request-scoped.
        services.AddScoped<Cnas.Ps.Application.Audit.IAuditExplorerService,
            Cnas.Ps.Infrastructure.Services.Audit.AuditExplorerService>();
        services.AddScoped<IDocumentService, DocumentServiceImpl>();
        services.AddScoped<IFormIntakeService, FormIntakeService>();
        services.AddScoped<IDocumentExaminationService, DocumentExaminationService>();
        // Annex 7 DOCX templates — registered as singletons because they are stateless
        // and thread-safe. The service collection resolves all IDocxTemplate
        // implementations into the IEnumerable<IDocxTemplate> constructor parameter on
        // DocumentGenerationService, which builds its case-insensitive code lookup at
        // construction time.
        services.AddSingleton<IDocxTemplate, DeciziaPensieTemplate>();
        services.AddSingleton<IDocxTemplate, FisaDeCalculTemplate>();
        services.AddSingleton<IDocxTemplate, DispozitiaInscriereTemplate>();
        services.AddSingleton<IDocxTemplate, InvitatieDocumenteSuplimentareTemplate>();
        services.AddSingleton<IDocxTemplate, RefuzAplicareTemplate>();
        services.AddSingleton<IDocxTemplate, CalcSporVechimeTemplate>();
        services.AddSingleton<IDocxTemplate, DispozitieRecalculTemplate>();
        services.AddSingleton<IDocxTemplate, AvizAvocatuluiPoporuluiTemplate>();
        services.AddSingleton<IDocxTemplate, AdresaSolicitantTemplate>();
        services.AddSingleton<IDocxTemplate, RaportControlInternTemplate>();
        services.AddSingleton<IDocxTemplate, CerereDocumenteLipsaTemplate>();
        services.AddSingleton<IDocxTemplate, AdeverintaCotizareTemplate>();
        services.AddSingleton<IDocxTemplate, AdresaCertificatTemplate>();
        services.AddSingleton<IDocxTemplate, DecizieRevocareTemplate>();
        services.AddSingleton<IDocxTemplate, DispozitieReluareTemplate>();
        services.AddSingleton<IDocxTemplate, AnsamblulAcordareDrepturiTemplate>();
        services.AddSingleton<IDocxTemplate, AvizComisieMedicalaTemplate>();
        services.AddSingleton<IDocxTemplate, ConfirmareSumaTemplate>();
        services.AddSingleton<IDocxTemplate, AdresaInstitutieMedicalaTemplate>();
        services.AddSingleton<IDocxTemplate, ScrisoareInformareTemplate>();
        // Annex 7 extension batch — additional administrative correspondence templates
        // covering payment suspension, expert medical opinions, inter-branch dossier
        // transfers, internal informational notes, and recalculation acknowledgements.
        services.AddSingleton<IDocxTemplate, DecizieSuspendarePlataTemplate>();
        services.AddSingleton<IDocxTemplate, AvizExpertizaMedicalaTemplate>();
        services.AddSingleton<IDocxTemplate, AdresaDosarSocialTemplate>();
        services.AddSingleton<IDocxTemplate, NotaInformareTemplate>();
        services.AddSingleton<IDocxTemplate, CerereRecalculPlataTemplate>();
        // Annex 7 extension batch (administrative correspondence — round 2): clarifying-question
        // letters, one-time aid dispositions, payment-method transfer notifications,
        // already-paid-amount adjustment decisions, and final audit opinions closing a
        // control engagement.
        services.AddSingleton<IDocxTemplate, AdresaIntrebareSuplimentaraTemplate>();
        services.AddSingleton<IDocxTemplate, DispozitieAjutorUnicTemplate>();
        services.AddSingleton<IDocxTemplate, NotificarePlataTransferataTemplate>();
        services.AddSingleton<IDocxTemplate, DecizieAjustareSumeTemplate>();
        services.AddSingleton<IDocxTemplate, AvizFinalControlTemplate>();
        // Annex 7 extension batch (administrative correspondence — round 3): final
        // cessation-of-payment decisions, personal-data-correction notifications,
        // citizen-facing dossier transfer requests, archival-closure notices, and
        // over-payment recovery decisions.
        services.AddSingleton<IDocxTemplate, DecizieIncetarePlataTemplate>();
        services.AddSingleton<IDocxTemplate, NotificareCorectareDateTemplate>();
        services.AddSingleton<IDocxTemplate, CerereTransferDosarTemplate>();
        services.AddSingleton<IDocxTemplate, AdresaArhivareTemplate>();
        services.AddSingleton<IDocxTemplate, DecizieRecuperareSumeTemplate>();
        // R2000 / R2002 / iter-145 — standard Cerere + Raport templates per
        // Annex 7 §8.7.1 / §8.7.3. Two Cereri (insured-person registration,
        // pension modification) and two Rapoarte (statistical payments,
        // audit activity).
        services.AddSingleton<IDocxTemplate, CerereInregistrareInsuredPersonTemplate>();
        services.AddSingleton<IDocxTemplate, CerereModificarePensieTemplate>();
        services.AddSingleton<IDocxTemplate, RaportStatisticaPlatiTemplate>();
        services.AddSingleton<IDocxTemplate, RaportAuditActivitateTemplate>();

        // A2 — MPay order persistence. Scoped because the store depends on the
        // per-request ICnasDbContext. Wired into both the outbound IMPayClient.PostOrderAsync
        // path (so callbacks always find a row) and the inbound MPayCallbackController
        // (so confirmations are idempotent — CLAUDE.md cross-cutting "Idempotent Callbacks").
        services.AddScoped<IMPayOrderStore, MPayOrderStore>();

        // UC17 phase 2A — Template-admin surface. Widened from singleton to scoped in
        // this batch because the service now depends on the per-request ICnasDbContext
        // (the persistent DocumentTemplates half of the catalog). The IEnumerable<IDocxTemplate>
        // collaborator remains a singleton set, but the union with the database must
        // happen per scope. See TemplateAdminService class-level XML doc for the
        // DI-vs-persistent collision rule and the IFileStorage dependency rationale.
        services.AddScoped<ITemplateAdminService, TemplateAdminService>();

        // UC17 phase 2B — Uploaded-template renderer. Scoped because it depends on
        // the per-request ICnasDbContext (operator-uploaded DocumentTemplate rows).
        // Consumed by DocumentGenerationService as a fallback when no DI-baked
        // IDocxTemplate matches the requested code; see UploadedTemplateRenderer XML
        // doc for the {{key}} substitution algorithm and the multi-run trade-off.
        services.AddScoped<IUploadedTemplateRenderer, UploadedTemplateRenderer>();

        // R0133 / R0134 — Tri-lingual template variants + XML/CSV catalog import-export.
        // All three components are scoped because they depend on per-request
        // ICnasDbContext, ICallerContext, and (for the catalog port) IAuditService.
        services.AddScoped<Cnas.Ps.Application.Templates.ITemplateVariantService, TemplateVariantService>();
        services.AddScoped<Cnas.Ps.Application.Templates.ITemplateVariantResolver, TemplateVariantResolver>();
        services.AddScoped<Cnas.Ps.Application.Templates.ITemplateCatalogPort, TemplateCatalogPort>();

        // R0131 / CF 17.15 — metadata-driven template validation gate. Scoped because
        // the implementation depends on the per-request IReadOnlyCnasDbContext seam.
        services.AddScoped<Cnas.Ps.Application.Templates.ITemplateValidationService,
            Cnas.Ps.Application.Templates.TemplateValidationService>();

        // R0143 / CF 17.19 — per-passport configuration matrix surface + the underlying
        // expression evaluator the matrix advertises. The evaluator is stateless and
        // safe as a singleton; the matrix service depends on the per-request
        // ICnasDbContext + ISqidService so it is scoped.
        services.AddSingleton<Cnas.Ps.Application.Calculations.IExpressionEvaluator,
            Cnas.Ps.Application.Calculations.ShuntingYardExpressionEvaluator>();
        services.AddScoped<Cnas.Ps.Application.UseCases.IServicePassportConfigMatrixService,
            ServicePassportConfigMatrixService>();

        // R2003 / R0133 — template-language coverage service (operational layer
        // on top of R0133 variant registry). Scoped because it consumes the
        // per-request ICnasDbContext + ICallerContext + IAuditService.
        // Validators are auto-registered via
        // AddValidatorsFromAssemblyContaining<ApplicationAssemblyMarker>.
        services.AddScoped<Cnas.Ps.Application.Templates.ITemplateLanguageCoverageService,
            Cnas.Ps.Infrastructure.Services.Templates.TemplateLanguageCoverageService>();

        services.AddScoped<IDocumentGenerationService, DocumentGenerationService>();
        services.AddScoped<IReportingService, ReportingService>();

        // R2461 / R2462 / Deliverables 7.1 + 7.2 — admin-triggered monthly
        // operational reports. Both services are pure-read (marked with
        // [LongRunningReportService]) and depend on the IReadOnlyCnasDbContext
        // seam + the FluentValidation validators auto-registered from the
        // Application assembly.
        services.AddScoped<Cnas.Ps.Application.Reporting.IMonthlySupportReportService,
            Cnas.Ps.Infrastructure.Services.Reporting.MonthlySupportReportService>();
        services.AddScoped<Cnas.Ps.Application.Reporting.IMonthlyErrorFixReportService,
            Cnas.Ps.Infrastructure.Services.Reporting.MonthlyErrorFixReportService>();

        services.AddScoped<IDecisionWorkflowService, DecisionWorkflowService>();
        // R0932 / TOR §10.1 — Fișa de calcul interactive recalculator. Stateless,
        // pure aggregation — singleton lifetime is safe.
        services.AddSingleton<Cnas.Ps.Application.UseCases.IFisaDeCalculRecalculator,
            Cnas.Ps.Infrastructure.Services.FisaDeCalculRecalculator>();
        // R0933 / TOR §10.1 — terminate-prior-on-acceptance lifecycle. Scoped
        // because it depends on the per-request ICnasDbContext + ICallerContext
        // + IAuditService + the optional INotificationTriggerDispatcher.
        services.AddScoped<Cnas.Ps.Application.UseCases.IPriorDecisionTerminator,
            Cnas.Ps.Infrastructure.Services.PriorDecisionTerminator>();
        // R0942 / TOR §10.1 — refused-pension → AlocatieSociala auto-fallback
        // cascade. Scoped because it depends on the per-request DbContext +
        // caller + audit + WorkflowOptions monitor.
        services.AddScoped<Cnas.Ps.Application.UseCases.IRefusedPensionFallbackCascade,
            Cnas.Ps.Infrastructure.Services.RefusedPensionFallbackCascade>();
        // R1502 / TOR §3.7-C — recompute pipeline for an existing benefit
        // decision. Scoped because it depends on the per-request DbContext +
        // audit + caller + notification dispatcher.
        services.AddScoped<Cnas.Ps.Application.UseCases.IDecisionRecomputeService,
            Cnas.Ps.Infrastructure.Services.DecisionRecomputeService>();
        // R1505 / TOR §3.7-F — recovery-workflow service. Scoped because it
        // depends on the per-request DbContext + audit + caller + notification
        // dispatcher.
        services.AddScoped<Cnas.Ps.Application.UseCases.IRecoveryDecisionService,
            Cnas.Ps.Infrastructure.Services.RecoveryDecisionService>();
        // R1504 / TOR §3.7-E — payment-suspension lifecycle service.
        // Scoped because it consumes the per-request DbContext + audit +
        // caller + notification dispatcher.
        services.AddScoped<Cnas.Ps.Application.UseCases.IPaymentSuspensionService,
            Cnas.Ps.Infrastructure.Services.PaymentSuspensionService>();
        services.AddScoped<FluentValidation.IValidator<Cnas.Ps.Contracts.PaymentSuspensionInputDto>,
            Cnas.Ps.Application.Validators.PaymentSuspensionInputValidator>();
        // R0115 / TOR CF 14.07 — MNotify template registry + bounce handler.
        services.AddScoped<Cnas.Ps.Application.UseCases.IMNotifyTemplateService,
            Cnas.Ps.Infrastructure.Services.MNotify.MNotifyTemplateService>();
        services.AddScoped<Cnas.Ps.Application.UseCases.IMNotifyBounceHandler,
            Cnas.Ps.Infrastructure.Services.MNotify.MNotifyBounceHandler>();
        services.AddScoped<FluentValidation.IValidator<Cnas.Ps.Contracts.MNotifyTemplateInputDto>,
            Cnas.Ps.Application.Validators.MNotifyTemplateInputValidator>();
        // R0116 + R0195 / TOR SEC 054-055 — MLog category-config service +
        // in-memory filter snapshot. The service is scoped; the filter is a
        // singleton because it caches the snapshot across requests.
        services.AddScoped<Cnas.Ps.Application.UseCases.IMLogCategoryConfigService,
            Cnas.Ps.Infrastructure.Services.MLog.MLogCategoryConfigService>();
        services.AddSingleton<Cnas.Ps.Infrastructure.Services.MLog.MLogCategoryFilter>();
        services.AddSingleton<Cnas.Ps.Application.UseCases.IMLogCategoryFilter>(
            sp => sp.GetRequiredService<Cnas.Ps.Infrastructure.Services.MLog.MLogCategoryFilter>());
        services.AddSingleton<Cnas.Ps.Infrastructure.Services.MLog.IMLogCategoryFilterCache>(
            sp => sp.GetRequiredService<Cnas.Ps.Infrastructure.Services.MLog.MLogCategoryFilter>());
        services.AddScoped<FluentValidation.IValidator<Cnas.Ps.Contracts.MLogCategoryConfigInputDto>,
            Cnas.Ps.Application.Validators.MLogCategoryConfigInputValidator>();
        // R0102 / TOR CF 14.02 — canonical CloudEvents envelope factory.
        services.AddScoped<Cnas.Ps.Application.MessageBus.IMConnectEnvelopeFactory,
            Cnas.Ps.Infrastructure.Services.MessageBus.MConnectEnvelopeFactory>();
        // R1601 / TOR Annex 3.9 — read-only RegistrulDeciziilor projection.
        services.AddScoped<Cnas.Ps.Application.Registers.IDecisionsRegister,
            Cnas.Ps.Infrastructure.Registers.DecisionsRegister>();
        // R1602 / TOR Annex 3.10 — read-only RegistrulConturilorDePlata projection.
        services.AddScoped<Cnas.Ps.Application.Registers.IBeneficiaryPaymentAccountsRegister,
            Cnas.Ps.Infrastructure.Registers.BeneficiaryPaymentAccountsRegister>();
        // R0590 / TOR CF 10.01 — decider's approval workspace projection. Scoped
        // because it depends on the per-request ICnasDbContext + ISqidService.
        services.AddScoped<IApprovalWorkspaceService, ApprovalWorkspaceService>();
        services.AddScoped<IProfileService, ProfileService>();

        // R0622 / TOR CF 13.03 — three management strategies (UI / Form /
        // ExternalSync) routed by ProfileManagementStrategyDispatcher. The
        // dispatcher is Singleton (no per-request state); strategies are
        // Scoped because they wrap the scoped IProfileService /
        // IFormIntakeService collaborators. The dispatcher resolves strategies
        // through IServiceScopeFactory so the singleton never captures a
        // scoped collaborator (CLAUDE.md DI-lifetime hygiene).
        services.AddScoped<Cnas.Ps.Application.Profile.IProfileManagementStrategy,
            Cnas.Ps.Infrastructure.Services.Profile.UiProfileManagementStrategy>();
        services.AddScoped<Cnas.Ps.Application.Profile.IProfileManagementStrategy,
            Cnas.Ps.Infrastructure.Services.Profile.FormProfileManagementStrategy>();
        services.AddScoped<Cnas.Ps.Application.Profile.IProfileManagementStrategy,
            Cnas.Ps.Infrastructure.Services.Profile.ExternalSyncProfileManagementStrategy>();
        services.AddSingleton<Cnas.Ps.Application.Profile.IProfileManagementStrategyDispatcher,
            Cnas.Ps.Infrastructure.Services.Profile.ProfileManagementStrategyDispatcher>();

        services.AddScoped<IInteropService, InteropService>();
        services.AddScoped<IServicePassportService, ServicePassportService>();
        // R0141 / TOR CF 15.03 — admin business-rule editor backing the
        // ServicePassport.DecisionRulesJson surface. Scoped because it
        // depends on the per-request ICnasDbContext + ICallerContext +
        // IAuditService.
        services.AddScoped<Cnas.Ps.Application.UseCases.IServicePassportRulesEditorService,
            ServicePassportRulesEditorService>();
        services.AddScoped<IWorkflowConfigurationService, WorkflowConfigurationService>();
        // R0402 / TOR CF 17.09 — Classifier reference-blocking guard. Scoped
        // because it consumes the per-request IReadOnlyCnasDbContext (R0026 /
        // ARH 025 read-replica routing). Consumed by ClassifierService below.
        services.AddScoped<Cnas.Ps.Application.Classifiers.IClassifierReferenceGuard,
            Cnas.Ps.Infrastructure.Services.Classifiers.ClassifierReferenceGuard>();
        services.AddScoped<IClassifierService, ClassifierService>();
        // R2163 / INT 004 — schema-driven new-service provisioning façade.
        // Scoped because it depends on the per-request ICnasDbContext +
        // ICallerContext + IAuditService + IWorkflowConfigurationService.
        services.AddScoped<IServiceCatalogConfigService, ServiceCatalogConfigService>();
        // R2190-R2200 / TOR §15.6 FLEX 006 — dynamic-entity-attributes EAV sidecar.
        // Scoped because it consumes the per-request ICnasDbContext + ICallerContext.
        services.AddScoped<IDynamicAttributeService, DynamicAttributeService>();
        // R0332 / TOR CF 12.02 — electronic-archive metadata summariser.
        // Scoped because it consumes the per-request IReadOnlyCnasDbContext
        // (R0026 / PSR 006 read-replica routing). Surfaced by
        // GET /api/archive/summary on the Web tabbed UI.
        services.AddScoped<Cnas.Ps.Application.Archive.IArchiveMetadataService,
            Cnas.Ps.Infrastructure.Services.Archive.ArchiveMetadataService>();
        services.AddScoped<IUserAdministrationService, UserAdministrationService>();

        // R0500 / TOR CF 01.02 — public KPI snapshot service. Singleton so the
        // in-process 5-minute cache survives across requests; the per-recompute
        // IReadOnlyCnasDbContext is materialised through IServiceScopeFactory
        // so the singleton never captures a scoped DbContext. Marked
        // [LongRunningReportService] — the architecture suite asserts the
        // service injects ONLY the read-replica context.
        services.AddSingleton<Cnas.Ps.Application.PublicServices.IPublicKpiService,
            Cnas.Ps.Infrastructure.Services.PublicServices.PublicKpiService>();

        // R0165 / CF 03.06 — saved registry searches. Scoped because the service depends on
        // the per-request ICnasDbContext + ICallerContext. Options bind from Cnas:SavedSearch
        // so operators can tune the per-owner cap and the filter-payload budget without
        // redeploying. Defaults are documented on SavedSearchOptions.
        services.AddOptions<SavedSearchOptions>()
            .Bind(configuration.GetSection(SavedSearchOptions.SectionName));
        services.AddScoped<ISavedSearchService, SavedSearchService>();

        // R0156 / TOR CF 09.02 / FLEX 003 — ad-hoc report builder. Both components are
        // Scoped because they depend on the per-request ICnasDbContext + ICallerContext.
        services.AddScoped<Cnas.Ps.Application.Reports.IReportTemplateService,
            Cnas.Ps.Infrastructure.Services.Reports.ReportTemplateService>();
        services.AddScoped<Cnas.Ps.Application.Reports.IReportEngine,
            Cnas.Ps.Infrastructure.Services.Reports.ReportEngine>();

        // R0583 / TOR CF 09.06 / CF 09.09 — background report runner. Both components
        // are Scoped because they depend on the per-request ICnasDbContext + the
        // R0227 IAttachmentService + R0171 INotificationService. The Quartz job
        // owns its own scope per fire (see ReportJobBackgroundJob.Execute).
        services.AddScoped<Cnas.Ps.Application.Reports.IReportJobService,
            Cnas.Ps.Infrastructure.Services.Reports.ReportJobService>();
        services.AddScoped<Cnas.Ps.Application.Reports.IReportJobRunner,
            Cnas.Ps.Infrastructure.Services.Reports.ReportJobRunner>();
        services.AddScoped<
            FluentValidation.IValidator<Cnas.Ps.Contracts.ReportJobEnqueueDto>,
            Cnas.Ps.Application.Validators.ReportJobEnqueueDtoValidator>();

        // R0321 / R0224 / UI 008 — application autosave / version-history service. Scoped
        // because it depends on the per-request ICnasDbContext + ICallerContext. Options
        // bind from Cnas:ApplicationAutosave so operators can tune the per-application
        // autosave cap + payload budget without redeploying. The validator is registered
        // alongside so FluentValidation picks it up by reflection.
        services.AddOptions<ApplicationAutosaveOptions>()
            .Bind(configuration.GetSection(ApplicationAutosaveOptions.SectionName));
        services.AddScoped<IApplicationVersionService, ApplicationVersionService>();
        services.AddScoped<
            FluentValidation.IValidator<Cnas.Ps.Contracts.ApplicationVersionSaveDto>,
            Cnas.Ps.Application.Validators.ApplicationVersionSaveDtoValidator>();

        // R0227 / TOR UI 014 — file-attachment widget server primitive. Options bind
        // from Cnas:Attachments. The blob storage adapter is a Singleton (stateless,
        // wraps in-memory options); the validator is Singleton (immutable allow-list);
        // the service is Scoped because it depends on the per-request ICnasDbContext +
        // ICallerContext. FluentValidation discovers the upload validator from the
        // Application-assembly scan but we wire it explicitly here to mirror the
        // ApplicationVersionSaveDtoValidator pattern.
        services.AddOptions<Cnas.Ps.Application.Attachments.AttachmentOptions>()
            .Bind(configuration.GetSection(Cnas.Ps.Application.Attachments.AttachmentOptions.SectionName));
        services.AddSingleton<Cnas.Ps.Application.Attachments.IBlobStorage,
            Cnas.Ps.Infrastructure.Storage.LocalDiskBlobStorage>();
        services.AddSingleton<Cnas.Ps.Application.Attachments.IAttachmentValidator,
            Cnas.Ps.Infrastructure.Services.Attachments.AttachmentValidator>();
        services.AddScoped<Cnas.Ps.Application.Attachments.IAttachmentService,
            Cnas.Ps.Infrastructure.Services.Attachments.AttachmentService>();
        services.AddScoped<
            FluentValidation.IValidator<Cnas.Ps.Contracts.AttachmentUploadDto>,
            Cnas.Ps.Application.Validators.AttachmentUploadDtoValidator>();

        // R0182 / SEC 042 — admin-configurable audit policies. The resolver is a
        // singleton (it owns the in-memory snapshot) wired into the drainer's flush
        // path. A hosted refresh job rebuilds the snapshot on a 60 s cadence; the
        // CRUD service additionally invalidates the snapshot synchronously after
        // every mutation. Options bind from Cnas:AuditPolicy.
        services.AddOptions<AuditPolicyOptions>()
            .Bind(configuration.GetSection(AuditPolicyOptions.SectionName));
        services.AddSingleton<AuditPolicyResolver>();
        services.AddSingleton<IAuditPolicyResolver>(sp => sp.GetRequiredService<AuditPolicyResolver>());
        services.AddHostedService<AuditPolicyCacheRefreshJob>();
        services.AddScoped<IAuditPolicyService, AuditPolicyService>();

        // R0183 / SEC 043 — per-entity field-policy registry + diff writer. Resolver
        // is a singleton (owns the ConcurrentDictionary snapshot) wired into the
        // scoped diff writer. The refresh job mirrors R0182's cadence + failure
        // policy. Options bind from Cnas:AuditFieldPolicy.
        services.AddOptions<AuditFieldPolicyOptions>()
            .Bind(configuration.GetSection(AuditFieldPolicyOptions.SectionName));
        services.AddSingleton<AuditFieldPolicyResolver>();
        services.AddSingleton<IAuditFieldPolicyResolver>(sp => sp.GetRequiredService<AuditFieldPolicyResolver>());
        services.AddHostedService<AuditFieldPolicyCacheRefreshJob>();
        services.AddScoped<IAuditFieldPolicyService, AuditFieldPolicyService>();
        services.AddSingleton<IAuditDiffComputer, AuditDiffComputer>();
        services.AddScoped<IAuditDiffWriter, AuditDiffWriter>();

        // R0128 / R0173 / CF 16.14 + CF 22.04 — per-workflow notification strategies.
        // Singleton resolver owns the in-memory snapshot; the CRUD service invalidates
        // it synchronously after every mutation and a background refresh job picks up
        // any out-of-band drift on a 60 s cadence. The orchestrator is scoped because
        // it depends on the per-request ICallerContext + IReadOnlyCnasDbContext.
        services.AddOptions<WorkflowNotificationStrategyOptions>()
            .Bind(configuration.GetSection(WorkflowNotificationStrategyOptions.SectionName));
        services.AddSingleton<WorkflowNotificationStrategyResolver>();
        services.AddSingleton<IWorkflowNotificationStrategyResolver>(
            sp => sp.GetRequiredService<WorkflowNotificationStrategyResolver>());
        services.AddHostedService<WorkflowNotificationStrategyCacheRefreshJob>();
        services.AddScoped<IWorkflowNotificationStrategyService, WorkflowNotificationStrategyService>();
        services.AddScoped<IWorkflowNotificationOrchestrator, WorkflowNotificationOrchestrator>();

        // R0210 / TOR UI 007 / CF 17.16 — centralised translation tool. Singleton
        // resolver owns the in-memory (code, language) → text snapshot; the value-side
        // CRUD service invalidates synchronously after every mutation and the background
        // refresh job rebuilds on a 60 s cadence. Both CRUD services are scoped because
        // they consume the per-request DbContext + caller context.
        services.AddOptions<TranslationOptions>()
            .Bind(configuration.GetSection(TranslationOptions.SectionName));
        services.AddSingleton<TranslationResolver>();
        services.AddSingleton<ITranslationResolver>(sp => sp.GetRequiredService<TranslationResolver>());
        services.AddHostedService<TranslationCacheRefreshJob>();
        services.AddScoped<ITranslationKeyService, TranslationKeyService>();
        services.AddScoped<ITranslationValueService, TranslationValueService>();

        // R0225 / TOR UI 015 — contextual-help registry. Same lifetime + cadence pattern
        // as the translation registry above. The resolver needs the Sqid encoder so it
        // can pre-render the DTO ids on the cached snapshot.
        services.AddOptions<HelpOptions>()
            .Bind(configuration.GetSection(HelpOptions.SectionName));
        services.AddSingleton<HelpResolver>();
        services.AddSingleton<IHelpResolver>(sp => sp.GetRequiredService<HelpResolver>());
        services.AddHostedService<HelpCacheRefreshJob>();
        services.AddScoped<IHelpTopicService, HelpTopicService>();
        services.AddScoped<IHelpTopicTranslationService, HelpTopicTranslationService>();

        // R0126 / CF 16.10 — workflow-scoped ACL. Singleton resolver owns the in-memory
        // ACL snapshot; the CRUD service invalidates it synchronously after every
        // mutation and a background refresh job picks up any out-of-band drift on a 60 s
        // cadence (default). Options bind from Cnas:WorkflowAcl.
        services.AddOptions<WorkflowAclOptions>()
            .Bind(configuration.GetSection(WorkflowAclOptions.SectionName));
        services.AddSingleton<WorkflowAclService>();
        services.AddSingleton<IWorkflowAclService>(sp => sp.GetRequiredService<WorkflowAclService>());
        services.AddHostedService<WorkflowAclCacheRefreshJob>();
        services.AddScoped<IWorkflowStepAclService, WorkflowStepAclService>();
        services.AddScoped<FluentValidation.IValidator<Cnas.Ps.Contracts.WorkflowStepAclUpsertInput>,
            Cnas.Ps.Application.Validators.WorkflowStepAclUpsertInputValidator>();

        // R0124 / CF 16.08 — workflow-lifecycle rule engine + decision-engine-backed
        // evaluator. The engine is scoped because it consumes the per-request
        // IReadOnlyCnasDbContext; the evaluator is a singleton bridge onto an
        // IRulePackBackend (R0124 continuation). Today the backend is the no-op
        // implementation (NoopRulePackBackend) which logs at Information and always
        // allows — the real DMN / JSON rule-pack runtime is gated on R1502 / R0942.
        // The InMemoryWorkflowRulePackEvaluator placeholder is intentionally NOT
        // registered any more; it remains reachable for unit tests that want a
        // dependency-free always-allow evaluator.
        services.AddSingleton<IRulePackBackend, NoopRulePackBackend>();
        services.AddSingleton<IWorkflowRulePackEvaluator, DecisionEngineBackedWorkflowRulePackEvaluator>();
        services.AddScoped<IWorkflowRuleEngine, WorkflowRuleEngine>();

        // R0123 / CF 16.05 — persisted workflow execution graph + deterministic
        // executor. Both depend on the per-request ICnasDbContext + ICallerContext so
        // they are scoped. The validator is also scoped to honour FluentValidation's
        // discovery conventions.
        services.AddScoped<Cnas.Ps.Application.Workflow.IWorkflowGraphService,
            Cnas.Ps.Infrastructure.Services.Workflow.WorkflowGraphService>();
        services.AddScoped<Cnas.Ps.Application.Workflow.IWorkflowGraphExecutor,
            Cnas.Ps.Infrastructure.Services.Workflow.WorkflowGraphExecutor>();
        services.AddScoped<FluentValidation.IValidator<Cnas.Ps.Contracts.WorkflowGraphInputDto>,
            Cnas.Ps.Application.Validators.WorkflowGraphInputDtoValidator>();

        // R0125 / CF 16.09 — workflow-task step history projection. Scoped because the
        // service consumes the per-request ICnasDbContext + ICallerContext.
        services.AddScoped<Cnas.Ps.Application.Workflow.IWorkflowTaskHistoryService,
            Cnas.Ps.Infrastructure.Services.Workflow.WorkflowTaskHistoryService>();
        services.AddScoped<FluentValidation.IValidator<Cnas.Ps.Contracts.WorkflowTaskHistoryFilterDto>,
            Cnas.Ps.Application.Validators.WorkflowTaskHistoryFilterDtoValidator>();

        // R0132 / CF 17.18 — template version history (list / diff / rollback). Scoped per
        // request to share the ICnasDbContext lifetime with the existing template admin
        // surface.
        services.AddScoped<Cnas.Ps.Application.Templates.ITemplateVersionHistoryService,
            Cnas.Ps.Infrastructure.Services.Templates.TemplateVersionHistoryService>();
        services.AddScoped<FluentValidation.IValidator<Cnas.Ps.Contracts.TemplateRollbackInputDto>,
            Cnas.Ps.Application.Validators.TemplateRollbackInputDtoValidator>();

        // R0059 / SEC 016 — Account state machine. Scoped per request because the service
        // captures the per-request ICnasDbContext + ICallerContext for audit attribution.
        // The auto-lock convenience path bypasses the role check; see service remarks.
        services.AddScoped<IUserAccountStateService, UserAccountStateService>();

        // R2264 / SEC 017 + R2267 / SEC 020 — session-limit enforcer + session lock
        // service. Both are scoped per request because they capture the per-request
        // ICnasDbContext + ICallerContext for audit attribution. The auth-pipeline
        // integration that calls RegisterNewSessionAsync at sign-in is deferred to a
        // follow-up batch — the service primitives are shipped here as the foundation.
        services.AddOptions<SessionLimitOptions>()
            .Bind(configuration.GetSection(SessionLimitOptions.SectionName));
        services.AddScoped<ISessionLimitEnforcer, SessionLimitEnforcer>();
        services.AddScoped<ISessionLockService, SessionLockService>();

        // R0051 / TOR SEC 014 / CLAUDE.md §5.3 — local username/password fallback
        // for the UtilizatorAutorizat persona. The failure tracker is the in-memory
        // sliding-window counter (Singleton, thread-safe); a Redis-backed swap will
        // land alongside the multi-replica deployment. The login service itself is
        // Scoped because it holds the per-request ICnasDbContext.
        services.AddSingleton<Cnas.Ps.Application.Identity.IFailedLoginAttemptTracker,
            Cnas.Ps.Infrastructure.Security.InMemoryFailedLoginAttemptTracker>();
        services.AddScoped<Cnas.Ps.Application.Identity.ILocalLoginService,
            Cnas.Ps.Infrastructure.Services.Identity.LocalLoginService>();

        // R0058 / SEC 027 — Maker-checker 4-eyes workflow. The service depends on the
        // per-request ICnasDbContext + ICallerContext so it is scoped. Executors are
        // registered as multiple-instance so the service can inject the full set via
        // IEnumerable<IPendingAdminActionExecutor> and dispatch to the first one that
        // claims the supplied operation code. The DEMO.NOOP placeholder gives the
        // pipeline an end-to-end smoke surface until a real destructive admin action
        // is retrofitted (see NoOpDemoExecutor remarks / TODO[r0058-retrofit]).
        services.AddScoped<IPendingAdminActionService, PendingAdminActionService>();
        services.AddScoped<IPendingAdminActionExecutor, NoOpDemoExecutor>();

        // R0057 / SEC 026 + CF 16.11 — delegation lifecycle. Scoped because the
        // service touches the per-request ICnasDbContext + ICallerContext + audit
        // service. The validators are static singletons inside the service itself
        // so no separate registration is needed.
        services.AddScoped<IDelegationLifecycleService, DelegationLifecycleService>();

        // R0830 / R0834 / TOR Annex 1 §8.1.4.5 — insolvency lifecycle. Scoped because
        // it touches the per-request ICnasDbContext + ICallerContext + clock + audit.
        // Validators are clock-anchored inside the service body so the future-date
        // guards stay deterministic under the injected ICnasTimeProvider.
        services.AddScoped<IInsolvencyLifecycleService, InsolvencyLifecycleService>();

        // R2273 / TOR SEC 027 — generic 4-eyes admin substrate. The service is scoped
        // because it touches the per-request ICnasDbContext + ICallerContext for audit
        // attribution. Policies + handlers are registered as multiple-instance so the
        // service can resolve IEnumerable<ISensitiveActionPolicy> /
        // IEnumerable<ISensitiveActionHandler> and dispatch by ActionCode. The initial
        // policy + handler set is empty — concrete sensitive actions are added by
        // future iterations via AddSensitiveActionPolicy<T>() /
        // AddSensitiveActionHandler<T>().
        services.AddScoped<Cnas.Ps.Application.SensitiveActions.ISensitiveActionRegistry,
            Cnas.Ps.Infrastructure.Services.SensitiveActions.SensitiveActionRegistry>();
        services.AddScoped<Cnas.Ps.Application.SensitiveActions.ISensitiveAdminActionService,
            Cnas.Ps.Infrastructure.Services.SensitiveActions.SensitiveAdminActionService>();

        // R2271 / TOR SEC 025 — ABAC business-rules engine. The parser is
        // stateless (Singleton); the evaluator owns an in-process parse cache
        // so it is also Singleton (it resolves a scoped DbContext on each
        // evaluation via the IServiceProvider). The registry service touches
        // the per-request DbContext + caller context, so it is Scoped. The
        // ASP.NET Core authorization plumbing (IAuthorizationPolicyProvider +
        // IAuthorizationHandler) lives in the Cnas.Ps.Api layer because
        // Infrastructure intentionally does not reference Microsoft.AspNetCore.*.
        services.AddSingleton<Cnas.Ps.Application.Abac.IAbacExpressionParser,
            Cnas.Ps.Infrastructure.Services.Abac.AbacExpressionParser>();
        services.AddSingleton<Cnas.Ps.Application.Abac.IAbacRuleEvaluator,
            Cnas.Ps.Infrastructure.Services.Abac.AbacRuleEvaluator>();
        services.AddScoped<Cnas.Ps.Application.Abac.IAbacRuleRegistryService,
            Cnas.Ps.Infrastructure.Services.Abac.AbacRuleRegistryService>();
        // UC18 supporting service — syncs MPass-authenticated principals into the local
        // UserProfiles table on every successful sign-in. Scoped because it depends on
        // the per-request DbContext.
        services.AddScoped<IUserDirectoryService, UserDirectoryService>();
        services.AddSingleton<IAutomationService, AutomationService>();
        // R0204 / TOR CF 20.07-08 — Quartz scheduler inspector for the admin Jobs dashboard.
        // Singleton because the implementation is stateless and only queries the scheduler.
        services.AddSingleton<IJobStateInspector, JobStateInspector>();
        // R0137 — application-level file-immutability ledger. Scoped because the marker
        // writes against the per-request EF Core context; the guard reads against the
        // same primary so a "mark then delete in the same request" sequence is consistent.
        services.AddScoped<IFileImmutabilityMarker, FileImmutabilityMarker>();
        services.AddScoped<IFileImmutabilityGuard, FileImmutabilityGuard>();
        services.AddScoped<IApplicationProcessingService, ApplicationProcessingService>();

        // Workflow engine (Operaton / Camunda 7 REST). Configuration is optional in dev —
        // when OperatonBaseUrl is empty, calls short-circuit to INTERNAL_ERROR without
        // producing HTTP traffic, so this registration is safe even without external infra.
        services.AddOptions<WorkflowOptions>()
            .Bind(configuration.GetSection(WorkflowOptions.SectionName))
            .ValidateOnStart();
        services.AddHttpClient<IWorkflowEngine, OperatonWorkflowEngine>()
            .ConfigureHttpClient((sp, c) =>
            {
                c.Timeout = TimeSpan.FromSeconds(30);
                c.DefaultRequestHeaders.UserAgent.ParseAdd("CNAS-PS/1.0");
            });

        // R0166 / CF 03.11 / UI 015 — server-side cross-page bulk-action stack:
        //   * BulkSelectionOptions / BulkOperationOptions bind from Cnas:BulkActions.
        //   * BulkSelectionService is scoped (depends on per-request DbContext).
        //   * BulkOperationRunner is scoped (depends on per-request DbContext + IAuditService).
        //   * Filter resolvers + the resolver factory are scoped because they depend on
        //     per-request DbContext; the factory is registered as scoped to match.
        //   * BulkOperationRegistry is a singleton: the dispatch table is invariant
        //     and the operations are looked up by code at runtime; each operation's
        //     IBulkOperation lifetime is independent (sample operation below is
        //     registered scoped because it depends on per-request DbContext).
        services.AddOptions<BulkSelectionOptions>()
            .Bind(configuration.GetSection(BulkSelectionOptions.SectionName));
        services.AddOptions<BulkOperationOptions>()
            .Bind(configuration.GetSection(BulkOperationOptions.SectionName));
        services.AddScoped<IBulkSelectionFilterResolver, SolicitantFilterResolver>();
        services.AddScoped<IBulkSelectionFilterResolver, CerereFilterResolver>();
        services.AddScoped<IBulkSelectionFilterResolver, WorkflowTaskFilterResolver>();
        services.AddScoped<IBulkSelectionFilterResolver, DecisionFilterResolver>();
        services.AddScoped<IBulkSelectionFilterResolverFactory, BulkSelectionFilterResolverFactory>();
        services.AddScoped<IBulkSelectionService, BulkSelectionService>();
        services.AddScoped<IBulkOperation, WorkflowTaskReassignBulkOperation>();
        // R0527 / CF 03.11 — additional bulk operations shipped with this batch.
        // Both are scoped because they depend on the per-request CnasDbContext.
        services.AddScoped<IBulkOperation, CerereChangeStatusBulkOperation>();
        services.AddScoped<IBulkOperation, WorkflowTaskMarkCompleteBulkOperation>();
        // R0305 / BP 1.8 — Annex 1 Contributor.ReassignBranch bulk operation.
        services.AddScoped<IBulkOperation, ContributorBulkReassignBranchOperation>();
        // Registry is singleton — built once at startup from the registered set. Note:
        // because IBulkOperation is registered as scoped, we resolve the set lazily
        // inside the registry via a synthetic singleton wrapper that opens a fresh DI
        // scope only at startup; here we keep it simple by registering as scoped to
        // match the operations' lifetime.
        services.AddScoped<IBulkOperationRegistry, BulkOperationRegistry>();
        services.AddScoped<IBulkOperationRunner, BulkOperationRunner>();

        // R0202 / CF 20.05 — bind the unclaimed-task escalation job options. The defaults
        // (4h window, hourly cron, 200-row batch cap) are appropriate for production and
        // can be tuned via the Cnas:WorkflowEscalation config section without redeploying.
        services.AddOptions<UnclaimedTaskEscalationOptions>()
            .Bind(configuration.GetSection(UnclaimedTaskEscalationOptions.SectionName))
            .ValidateOnStart();

        // R0201 / CF 20.02 — KPI pre-aggregation. The orchestrator is scoped
        // (depends on the per-request DbContext); each calculator is scoped
        // because it reads through the per-request IReadOnlyCnasDbContext.
        // Calculators are registered as IEnumerable<IKpiCalculator> — adding
        // a new KPI is a one-line registration here (plus the implementation).
        services.AddScoped<Cnas.Ps.Application.Kpi.IKpiSnapshotService,
            Cnas.Ps.Infrastructure.Services.Kpi.KpiSnapshotService>();
        services.AddScoped<Cnas.Ps.Application.Kpi.IKpiCalculator,
            Cnas.Ps.Infrastructure.Kpi.Calculators.ApplicationsPendingCalculator>();
        services.AddScoped<Cnas.Ps.Application.Kpi.IKpiCalculator,
            Cnas.Ps.Infrastructure.Kpi.Calculators.ApplicationsClosedYesterdayCalculator>();
        services.AddScoped<Cnas.Ps.Application.Kpi.IKpiCalculator,
            Cnas.Ps.Infrastructure.Kpi.Calculators.TasksOverdueCalculator>();
        services.AddScoped<Cnas.Ps.Application.Kpi.IKpiCalculator,
            Cnas.Ps.Infrastructure.Kpi.Calculators.TasksAverageHandlingTimeCalculator>();
        services.AddScoped<Cnas.Ps.Application.Kpi.IKpiCalculator,
            Cnas.Ps.Infrastructure.Kpi.Calculators.NotificationsDeliveredYesterdayCalculator>();

        // R0537 / CF 04.10 — Admin dashboard superset. Scoped because the orchestrator
        // depends on the per-request IReadOnlyCnasDbContext + ICnasTimeProvider; the
        // KPI snapshot service it consumes is itself scoped.
        services.AddScoped<Cnas.Ps.Application.AdminDashboard.IAdminDashboardService,
            Cnas.Ps.Infrastructure.Services.AdminDashboardService>();

        // R0701 / TOR CF 21.01-02 — single-payload "open application dossier"
        // aggregator. Scoped because the orchestrator captures the per-request
        // IReadOnlyCnasDbContext + ICnasTimeProvider + ICallerContext + audit
        // sink, and delegates to the scoped IPrefillService for the
        // HasUnappliedPrefill probe.
        services.AddScoped<
            Cnas.Ps.Application.ApplicationProcessing.IApplicationProcessingContextService,
            Cnas.Ps.Infrastructure.Services.ApplicationProcessing.ApplicationProcessingContextService>();

        // R0535 / CF 04.07-08 — Per-user UI layout preferences. Scoped because the
        // service depends on the per-request ICnasDbContext + ICallerContext +
        // ICnasTimeProvider. The validator is auto-registered via
        // AddValidatorsFromAssemblyContaining<ApplicationAssemblyMarker> in
        // Application.AddCnasApplication.
        services.AddScoped<Cnas.Ps.Application.UserLayout.IUserLayoutPreferencesService,
            Cnas.Ps.Infrastructure.Services.UserLayoutPreferencesService>();

        // R0153 / CF 19.05 — Contributor period-aware projection. Scoped because
        // the orchestrator depends on the per-request DbContext. Adding a new
        // projector mirrors the KPI registration pattern above.
        services.AddScoped<Cnas.Ps.Application.Etl.IContributorPeriodProjectionService,
            Cnas.Ps.Infrastructure.Services.Etl.ContributorPeriodProjectionService>();

        // R0911 / TOR BP 2.2-B — Treasury payment-receipt registry + per-receipt
        // distribution. Scoped because the implementation depends on the
        // per-request ICnasDbContext + ICallerContext for audit attribution.
        services.AddScoped<Cnas.Ps.Application.Treasury.ITreasuryPaymentService,
            Cnas.Ps.Infrastructure.Services.Treasury.TreasuryPaymentService>();

        // R0912 / TOR BP 2.2-C — social-insurance contract lifecycle
        // (issue / modify / terminate). Scoped because the implementation
        // depends on the per-request ICnasDbContext + ICallerContext for
        // audit attribution.
        services.AddScoped<Cnas.Ps.Application.Contributors.ISocialInsuranceContractService,
            Cnas.Ps.Infrastructure.Services.Contributors.SocialInsuranceContractService>();

        // R0831 / R0832 / TOR BP 1.3-B + BP 1.3-C — claims (creanțe) registry
        // and per-claim payment-application path. Scoped because the
        // implementation depends on the per-request ICnasDbContext +
        // ICallerContext for audit attribution.
        services.AddScoped<Cnas.Ps.Application.Claims.IClaimService,
            Cnas.Ps.Infrastructure.Services.Claims.ClaimService>();

        // R0814 / TOR BP 1.2-E — BASS-to-payer refund workflow. Scoped
        // because the implementation depends on the per-request
        // ICnasDbContext + ICallerContext for audit attribution.
        services.AddScoped<Cnas.Ps.Application.Financials.IBassRefundService,
            Cnas.Ps.Infrastructure.Services.Financials.BassRefundService>();

        // R0815 / TOR BP 1.2-F — Treasury-payment-correction workflow.
        // Scoped because the implementation depends on the per-request
        // ICnasDbContext + ICallerContext for audit attribution.
        services.AddScoped<Cnas.Ps.Application.Financials.IPaymentCorrectionService,
            Cnas.Ps.Infrastructure.Services.Financials.PaymentCorrectionService>();

        // R0816 / TOR BP 1.2-G — Treasury information export. Scoped because
        // the implementation depends on the per-request ICnasDbContext + the
        // injected Sqid service.
        services.AddScoped<Cnas.Ps.Application.Financials.ITreasuryInformationExporter,
            Cnas.Ps.Infrastructure.Services.Financials.TreasuryInformationExporter>();

        // R0817 / TOR BP 1.2-H — staggered-penalty-repayment service. Scoped
        // because the implementation depends on the per-request ICnasDbContext
        // + ICallerContext for audit attribution.
        services.AddScoped<Cnas.Ps.Application.Financials.IPenaltyRepaymentService,
            Cnas.Ps.Infrastructure.Services.Financials.PenaltyRepaymentService>();

        // R1600 / R1406 / TOR Annex 3.8 / §3.6-G — executory-documents
        // registry, withholding calculator and unemployment-benefit applier.
        // Scoped because the registry service depends on per-request
        // ICnasDbContext + ICallerContext for audit attribution; the
        // calculator is request-scoped too because it shares the same
        // DbContext for its lookup.
        services.AddScoped<Cnas.Ps.Application.ExecutoryDocuments.IExecutoryDocumentService,
            Cnas.Ps.Infrastructure.Services.ExecutoryDocuments.ExecutoryDocumentService>();
        services.AddScoped<Cnas.Ps.Application.ExecutoryDocuments.IExecutoryDocumentWithholdingCalculator,
            Cnas.Ps.Infrastructure.Services.ExecutoryDocuments.ExecutoryDocumentWithholdingCalculator>();
        services.AddScoped<Cnas.Ps.Application.ExecutoryDocuments.IUnemploymentBenefitWithholdingApplier,
            Cnas.Ps.Infrastructure.Services.ExecutoryDocuments.UnemploymentBenefitWithholdingApplier>();

        // R2270 / TOR SEC 023-024 — user-group registry + transitive role resolver.
        // Scoped because both services depend on per-request ICnasDbContext +
        // ICallerContext for audit attribution. Validators are auto-registered
        // via AddValidatorsFromAssemblyContaining<ApplicationAssemblyMarker>.
        services.AddScoped<Cnas.Ps.Application.Identity.IUserGroupService,
            Cnas.Ps.Infrastructure.Services.Identity.UserGroupService>();
        services.AddScoped<Cnas.Ps.Application.Identity.IUserGroupRoleResolver,
            Cnas.Ps.Infrastructure.Services.Identity.UserGroupRoleResolver>();

        // R2274 / TOR SEC 028 — "who can do what" access-rights report service.
        // Scoped because the service depends on per-request IReadOnlyCnasDbContext
        // + ICallerContext for audit attribution. Validators auto-register via
        // AddValidatorsFromAssemblyContaining<ApplicationAssemblyMarker>.
        services.AddScoped<Cnas.Ps.Application.Identity.IAccessRightsReportService,
            Cnas.Ps.Infrastructure.Services.Identity.AccessRightsReportService>();

        // R2279 / TOR SEC 033 — classification-catalog scanner + service.
        // The scanner is a stateless reflection cache (singleton); the
        // service is scoped because it consumes the per-request
        // ICnasDbContext + ICallerContext + IAuditService. Validators are
        // auto-registered via AddValidatorsFromAssemblyContaining<ApplicationAssemblyMarker>.
        services.AddSingleton<Cnas.Ps.Application.DataClassification.IClassificationCatalogScanner,
            Cnas.Ps.Infrastructure.Services.DataClassification.ClassificationCatalogScanner>();
        services.AddScoped<Cnas.Ps.Application.DataClassification.IClassificationCatalogService,
            Cnas.Ps.Infrastructure.Services.DataClassification.ClassificationCatalogService>();

        // R2282 / TOR SEC 036 — row-integrity check service + every concrete
        // IIntegrityCheck. The job resolves IEnumerable<IIntegrityCheck> so
        // a new check ships by adding ONE AddScoped line below. Scoped
        // because the checks each touch the per-request IReadOnlyCnasDbContext.
        services.AddScoped<Cnas.Ps.Application.Integrity.IIntegrityCheckContext,
            Cnas.Ps.Infrastructure.Services.Integrity.IntegrityCheckContext>();
        services.AddScoped<Cnas.Ps.Application.Integrity.IIntegrityCheckService,
            Cnas.Ps.Infrastructure.Services.Integrity.IntegrityCheckService>();
        services.AddScoped<Cnas.Ps.Application.Integrity.IIntegrityCheck,
            Cnas.Ps.Infrastructure.Services.Integrity.Checks.ClaimPaidAmountSumCheck>();
        services.AddScoped<Cnas.Ps.Application.Integrity.IIntegrityCheck,
            Cnas.Ps.Infrastructure.Services.Integrity.Checks.ClaimStatusCoherenceCheck>();
        services.AddScoped<Cnas.Ps.Application.Integrity.IIntegrityCheck,
            Cnas.Ps.Infrastructure.Services.Integrity.Checks.ExecutoryDocumentWithholdingCapCheck>();
        services.AddScoped<Cnas.Ps.Application.Integrity.IIntegrityCheck,
            Cnas.Ps.Infrastructure.Services.Integrity.Checks.UserProfileNationalIdHashSyncCheck>();
        services.AddScoped<Cnas.Ps.Application.Integrity.IIntegrityCheck,
            Cnas.Ps.Infrastructure.Services.Integrity.Checks.UserGroupMembershipOrphanCheck>();
        services.AddScoped<Cnas.Ps.Application.Integrity.IIntegrityCheck,
            Cnas.Ps.Infrastructure.Services.Integrity.Checks.TreasuryReceiptAmountSumCheck>();
        // R2709 / TOR SEC 053 — wires the R0194 audit-chain verifier into the
        // integrity-sweep surface so chain breaks light up the same dashboard
        // and acknowledgement workflow as every other invariant violation.
        services.AddScoped<Cnas.Ps.Application.Integrity.IIntegrityCheck,
            Cnas.Ps.Infrastructure.Services.Integrity.Checks.AuditChainIntegrityCheck>();

        // R1906 / TOR Annex 6 — per-report distribution rules + dispatcher.
        // Scoped because every service touches the per-request DbContext +
        // ICallerContext for audit attribution. The four channel handlers
        // are registered as IReportDistributionChannelHandler; the dispatcher
        // resolves them as IEnumerable and selects by Channel.
        services.AddScoped<Cnas.Ps.Application.Reporting.IReportDistributionService,
            Cnas.Ps.Infrastructure.Services.Reporting.ReportDistributionService>();
        services.AddScoped<Cnas.Ps.Application.Reporting.IReportDistributionDispatcher,
            Cnas.Ps.Infrastructure.Services.Reporting.ReportDistributionDispatcher>();
        services.AddScoped<Cnas.Ps.Application.Reporting.IReportRecipientResolver,
            Cnas.Ps.Infrastructure.Services.Reporting.ReportRecipientResolver>();
        services.AddScoped<Cnas.Ps.Application.Reporting.IReportDistributionChannelHandler,
            Cnas.Ps.Infrastructure.Services.Reporting.Channels.InSystemReportDistributionChannelHandler>();
        services.AddScoped<Cnas.Ps.Application.Reporting.IReportDistributionChannelHandler,
            Cnas.Ps.Infrastructure.Services.Reporting.Channels.DashboardReportDistributionChannelHandler>();
        services.AddScoped<Cnas.Ps.Application.Reporting.IReportDistributionChannelHandler,
            Cnas.Ps.Infrastructure.Services.Reporting.Channels.EmailReportDistributionChannelHandler>();
        services.AddScoped<Cnas.Ps.Application.Reporting.IReportDistributionChannelHandler,
            Cnas.Ps.Infrastructure.Services.Reporting.Channels.MNotifyReportDistributionChannelHandler>();

        // R1202 / TOR §3.4-C — capitalised periodic payments engine.
        // The mortality table + present-value calculator are singletons
        // (stateless, thread-safe pure functions). The service is scoped
        // because it shares the per-request ICnasDbContext + ICallerContext
        // for audit attribution.
        services.AddSingleton<Cnas.Ps.Application.CapitalisedPayments.IMortalityTable,
            Cnas.Ps.Infrastructure.Services.CapitalisedPayments.MoldovaPlaceholderMortalityTable>();
        services.AddSingleton<Cnas.Ps.Application.CapitalisedPayments.IPresentValueAnnuityCalculator,
            Cnas.Ps.Infrastructure.Services.CapitalisedPayments.PresentValueAnnuityCalculator>();
        services.AddScoped<Cnas.Ps.Application.CapitalisedPayments.ICapitalisedPaymentService,
            Cnas.Ps.Infrastructure.Services.CapitalisedPayments.CapitalisedPaymentService>();

        // R1403 / TOR §3.6-D — lifetime athlete-pension registry. The
        // eligibility evaluator + amount calculator are pure-function
        // singletons (stateless, thread-safe). The service is scoped because
        // it shares the per-request ICnasDbContext + ICallerContext for
        // audit attribution.
        services.AddSingleton<Cnas.Ps.Application.AthletePensions.IAthletePensionEligibilityEvaluator,
            Cnas.Ps.Infrastructure.Services.AthletePensions.AthletePensionEligibilityEvaluator>();
        services.AddSingleton<Cnas.Ps.Application.AthletePensions.IAthletePensionAmountCalculator,
            Cnas.Ps.Infrastructure.Services.AthletePensions.AthletePensionAmountCalculator>();
        services.AddScoped<Cnas.Ps.Application.AthletePensions.IAthletePensionAwardService,
            Cnas.Ps.Infrastructure.Services.AthletePensions.AthletePensionAwardService>();

        // R1201 / R1402 / TOR §3.4-B / §3.6-C — international-agreements
        // 3-level routing engine. The service is scoped because it owns the
        // per-request ICnasDbContext + ICallerContext for audit attribution.
        // Per-benefit-kind policies register as Scoped against the same
        // IIntlAgreementRoutingPolicy contract so the service can resolve
        // them by BenefitKind at request time.
        services.AddScoped<Cnas.Ps.Application.IntlAgreements.IIntlAgreementRoutingPolicy,
            Cnas.Ps.Infrastructure.Services.IntlAgreements.Policies.IncapacityMaternityIntlAgreementRoutingPolicy>();
        services.AddScoped<Cnas.Ps.Application.IntlAgreements.IIntlAgreementRoutingPolicy,
            Cnas.Ps.Infrastructure.Services.IntlAgreements.Policies.UnemploymentIntlAgreementRoutingPolicy>();
        services.AddScoped<Cnas.Ps.Application.IntlAgreements.IIntlAgreementRoutingService,
            Cnas.Ps.Infrastructure.Services.IntlAgreements.IntlAgreementRoutingService>();

        // R1503 / TOR §3.7-D — mass-recalculation engine. The strategy
        // collection ships EMPTY (no concrete per-benefit-kind strategies in
        // this iteration); operators register them via
        // AddBenefitRecalculationStrategy<T>() in subsequent iterations.
        // Decisions in scope without a registered strategy are tagged Skipped
        // with reason NO_STRATEGY_REGISTERED.
        services.AddScoped<Cnas.Ps.Application.Recalculation.ILegalChangeEventService,
            Cnas.Ps.Infrastructure.Services.Recalculation.LegalChangeEventService>();
        services.AddScoped<Cnas.Ps.Infrastructure.Services.Recalculation.MassRecalculationOrchestrator>();
        services.AddScoped<Cnas.Ps.Application.Recalculation.IMassRecalculationService,
            Cnas.Ps.Infrastructure.Services.Recalculation.MassRecalculationService>();

        // R1710 / TOR INT 002 — offline-batch (file-based) Annex-4 surface.
        // Schema registry + blob store are stateless singletons; the
        // submission service + processor are scoped because they consume
        // request-scoped ICnasDbContext + ICallerContext. The HMAC signer
        // is singleton (key loaded once at construction); options bind from
        // Cnas:BatchResponseSigning.
        services.AddOptions<Cnas.Ps.Application.Interop.Batch.BatchResponseSigningOptions>()
            .Bind(configuration.GetSection(Cnas.Ps.Application.Interop.Batch.BatchResponseSigningOptions.SectionName));
        services.AddSingleton<Cnas.Ps.Application.Interop.Batch.IOfflineBatchOpSchemaRegistry,
            Cnas.Ps.Infrastructure.Services.Interop.Batch.OfflineBatchOpSchemaRegistry>();
        services.AddSingleton<Cnas.Ps.Application.Interop.Batch.IOfflineBatchBlobStore,
            Cnas.Ps.Infrastructure.Services.Interop.Batch.InMemoryOfflineBatchBlobStore>();
        services.AddSingleton<Cnas.Ps.Application.Interop.Batch.IBatchResponseSigner,
            Cnas.Ps.Infrastructure.Services.Interop.Batch.HmacSha256BatchResponseSigner>();
        services.AddScoped<Cnas.Ps.Application.Interop.Batch.IOfflineBatchRequestParser,
            Cnas.Ps.Infrastructure.Services.Interop.Batch.OfflineBatchRequestParser>();
        services.AddScoped<Cnas.Ps.Application.Interop.Batch.IOfflineBatchSubmissionService,
            Cnas.Ps.Infrastructure.Services.Interop.Batch.OfflineBatchSubmissionService>();
        services.AddScoped<Cnas.Ps.Application.Interop.Batch.IOfflineBatchProcessor,
            Cnas.Ps.Infrastructure.Services.Interop.Batch.OfflineBatchProcessor>();
        services.AddScoped<
            FluentValidation.IValidator<Cnas.Ps.Contracts.OfflineBatchSubmissionInputDto>,
            Cnas.Ps.Application.Validators.OfflineBatchSubmissionInputValidator>();
        services.AddScoped<
            FluentValidation.IValidator<Cnas.Ps.Contracts.OfflineBatchReasonInputDto>,
            Cnas.Ps.Application.Validators.OfflineBatchReasonInputValidator>();
        services.AddScoped<
            FluentValidation.IValidator<Cnas.Ps.Contracts.OfflineBatchSubmissionFilterDto>,
            Cnas.Ps.Application.Validators.OfflineBatchSubmissionFilterValidator>();
        services.AddScoped<
            FluentValidation.IValidator<Cnas.Ps.Contracts.OfflineBatchRowFilterDto>,
            Cnas.Ps.Application.Validators.OfflineBatchRowFilterValidator>();

        // R2161 / TOR INT 002 — generic CnasUser-facing offline-batch service.
        // Sits alongside the R1710 Annex-4 B2B file-based registry above; both
        // are exposed but cover different consumers (admin/B2B vs end-user
        // ad-hoc ingest+export).
        services.AddScoped<Cnas.Ps.Application.Interop.Batch.IOfflineBatchService,
            Cnas.Ps.Infrastructure.Services.Interop.Batch.OfflineBatchService>();

        // R1810 / TOR BP 1.2-I — daily Treasury feed import. The in-memory
        // source is the singleton default (test fixture seeding stays in
        // process); the HTTPS placeholder is registered alongside so future
        // iterations can flip the default via UseHttpsTreasuryFeedSource().
        // Parser + importer + admin service are scoped because each depends
        // on per-request ICnasDbContext + ICallerContext for audit attribution.
        services.AddOptions<Cnas.Ps.Application.Treasury.Feed.TreasuryFeedOptions>()
            .Bind(configuration.GetSection(Cnas.Ps.Application.Treasury.Feed.TreasuryFeedOptions.SectionName));
        services.AddSingleton<Cnas.Ps.Infrastructure.Services.Treasury.Feed.InMemoryTreasuryFeedSource>();
        services.AddSingleton<Cnas.Ps.Application.Treasury.Feed.ITreasuryFeedSource>(
            sp => sp.GetRequiredService<Cnas.Ps.Infrastructure.Services.Treasury.Feed.InMemoryTreasuryFeedSource>());
        services.AddScoped<Cnas.Ps.Application.Treasury.Feed.ITreasuryFeedParser,
            Cnas.Ps.Infrastructure.Services.Treasury.Feed.TreasuryFeedParser>();
        services.AddScoped<Cnas.Ps.Application.Treasury.Feed.ITreasuryFeedImporter,
            Cnas.Ps.Infrastructure.Services.Treasury.Feed.TreasuryFeedImporter>();
        services.AddScoped<Cnas.Ps.Application.Treasury.Feed.ITreasuryFeedAdminService,
            Cnas.Ps.Infrastructure.Services.Treasury.Feed.TreasuryFeedAdminService>();
        services.AddScoped<
            FluentValidation.IValidator<Cnas.Ps.Contracts.TreasuryFeedImportFilterDto>,
            Cnas.Ps.Application.Validators.TreasuryFeedImportFilterValidator>();
        services.AddScoped<
            FluentValidation.IValidator<Cnas.Ps.Contracts.TreasuryFeedImportRowFilterDto>,
            Cnas.Ps.Application.Validators.TreasuryFeedImportRowFilterValidator>();

        // R1900-R1905 / iter-145 — Annex 6 report-catalog seed service.
        // The seed table lives at Cnas.Ps.Application.Reporting.ReportCatalogDescriptors;
        // refresh is exposed via /api/admin/reports/catalog/refresh under cnas-admin.
        services.AddScoped<Cnas.Ps.Application.Reporting.IReportCatalogSeedService,
            Cnas.Ps.Infrastructure.Services.Reporting.ReportCatalogSeedService>();

        // R0203 / TOR CF 20.06 — per-source external-system ingestion
        // framework. Connectors register as IEnumerable; the ingestion
        // service picks one by SourceCode and falls back to the in-memory
        // placeholder for unconfigured sources. PdfA + hash-verifier wired
        // here too (R0341).
        services.AddOptions<Cnas.Ps.Application.ExternalSources.ExternalSourceOptions>()
            .Bind(configuration.GetSection(Cnas.Ps.Application.ExternalSources.ExternalSourceOptions.SectionName));
        services.AddSingleton<Cnas.Ps.Infrastructure.Services.ExternalSources.InMemoryExternalSourceConnector>();
        services.AddSingleton<Cnas.Ps.Application.ExternalSources.IExternalSourceConnector>(
            sp => sp.GetRequiredService<Cnas.Ps.Infrastructure.Services.ExternalSources.RspExternalSourceConnector>());
        services.AddSingleton<Cnas.Ps.Infrastructure.Services.ExternalSources.RspExternalSourceConnector>();
        services.AddScoped<Cnas.Ps.Application.ExternalSources.IExternalSourceIngestionService,
            Cnas.Ps.Infrastructure.Services.ExternalSources.ExternalSourceIngestionService>();
        services.AddScoped<
            FluentValidation.IValidator<Cnas.Ps.Contracts.ExternalSourceManualTriggerInputDto>,
            Cnas.Ps.Application.Validators.ExternalSourceManualTriggerInputValidator>();
        services.AddScoped<
            FluentValidation.IValidator<Cnas.Ps.Contracts.ExternalSourceIngestionRunFilterDto>,
            Cnas.Ps.Application.Validators.ExternalSourceIngestionRunFilterValidator>();

        // R0341 / TOR CF 11.06 — PDF/A conversion + document hash verifier.
        // The conversion engine is placeholder until a license-cleared
        // library is approved; the hash verifier is fully production-ready
        // and runs against the existing Document.ContentSha256Hex column.
        services.AddOptions<Cnas.Ps.Infrastructure.Services.Documents.PdfAConversionOptions>()
            .Bind(configuration.GetSection(Cnas.Ps.Infrastructure.Services.Documents.PdfAConversionOptions.SectionName));
        services.AddSingleton<Cnas.Ps.Application.Documents.IPdfAConversionService,
            Cnas.Ps.Infrastructure.Services.Documents.PdfAConversionService>();
        services.AddScoped<Cnas.Ps.Application.Documents.IDocumentHashVerifier,
            Cnas.Ps.Infrastructure.Services.Documents.DocumentHashVerifier>();
        services.AddScoped<
            FluentValidation.IValidator<Cnas.Ps.Contracts.PdfAConversionInputDto>,
            Cnas.Ps.Application.Validators.PdfAConversionInputValidator>();

        // R2430 / R2431 / R2433 / TOR M4 — migration framework. In-memory
        // source is the singleton default (test fixture seeding stays in
        // process); future iterations swap in real legacy-system adapters.
        // The mapper registry is open-ended — concrete mappers are appended
        // alongside the identity wildcard fallback.
        services.AddSingleton<Cnas.Ps.Infrastructure.Services.Migration.InMemoryMigrationSource>();
        services.AddSingleton<Cnas.Ps.Application.Migration.IMigrationSource>(
            sp => sp.GetRequiredService<Cnas.Ps.Infrastructure.Services.Migration.InMemoryMigrationSource>());
        services.AddScoped<Cnas.Ps.Application.Migration.IMigrationRecordMapper,
            Cnas.Ps.Infrastructure.Services.Migration.IdentityMigrationRecordMapper>();
        services.AddScoped<Cnas.Ps.Application.Migration.IMigrationPlanService,
            Cnas.Ps.Infrastructure.Services.Migration.MigrationPlanService>();
        services.AddScoped<Cnas.Ps.Application.Migration.IMigrationImporter,
            Cnas.Ps.Infrastructure.Services.Migration.MigrationImporter>();
        services.AddScoped<Cnas.Ps.Application.Migration.IMigrationReconciler,
            Cnas.Ps.Infrastructure.Services.Migration.MigrationReconciler>();
        services.AddScoped<Cnas.Ps.Application.Migration.IMigrationAdminService,
            Cnas.Ps.Infrastructure.Services.Migration.MigrationAdminService>();
        services.AddScoped<
            FluentValidation.IValidator<Cnas.Ps.Contracts.MigrationPlanCreateInputDto>,
            Cnas.Ps.Application.Validators.MigrationPlanCreateInputValidator>();
        services.AddScoped<
            FluentValidation.IValidator<Cnas.Ps.Contracts.MigrationPlanModifyInputDto>,
            Cnas.Ps.Application.Validators.MigrationPlanModifyInputValidator>();
        services.AddScoped<
            FluentValidation.IValidator<Cnas.Ps.Contracts.MigrationPlanReasonInputDto>,
            Cnas.Ps.Application.Validators.MigrationPlanReasonInputValidator>();
        services.AddScoped<
            FluentValidation.IValidator<Cnas.Ps.Contracts.MigrationFindingAcknowledgeInputDto>,
            Cnas.Ps.Application.Validators.MigrationFindingAcknowledgeInputValidator>();
        services.AddScoped<
            FluentValidation.IValidator<Cnas.Ps.Contracts.MigrationFindingFilterDto>,
            Cnas.Ps.Application.Validators.MigrationFindingFilterValidator>();
        services.AddScoped<
            FluentValidation.IValidator<Cnas.Ps.Contracts.MigrationRunFilterDto>,
            Cnas.Ps.Application.Validators.MigrationRunFilterValidator>();
        services.AddScoped<
            FluentValidation.IValidator<Cnas.Ps.Contracts.MigrationRunDetailsFilterDto>,
            Cnas.Ps.Application.Validators.MigrationRunDetailsFilterValidator>();
        services.AddScoped<
            FluentValidation.IValidator<Cnas.Ps.Contracts.MigrationPlanFilterDto>,
            Cnas.Ps.Application.Validators.MigrationPlanFilterValidator>();

        // R2307 / TOR SEC 060 — backup-orchestration framework. The in-memory
        // target is registered as the singleton default; the S3-compatible
        // placeholder ships alongside it so the orchestrator can resolve
        // a target for either TargetKind without throwing. Production
        // swaps S3CompatibleBackupTarget for the real adapter in a later
        // iteration. Payload providers are registered per BackupScope.
        services.AddOptions<Cnas.Ps.Infrastructure.Services.Backups.BackupOptions>()
            .Bind(configuration.GetSection(Cnas.Ps.Infrastructure.Services.Backups.BackupOptions.SectionName));

        services.AddSingleton<Cnas.Ps.Infrastructure.Services.Backups.InMemoryBackupTarget>();
        services.AddSingleton<Cnas.Ps.Application.Backups.IBackupTarget>(sp =>
            sp.GetRequiredService<Cnas.Ps.Infrastructure.Services.Backups.InMemoryBackupTarget>());
        services.AddSingleton<Cnas.Ps.Application.Backups.IBackupTarget,
            Cnas.Ps.Infrastructure.Services.Backups.S3CompatibleBackupTarget>();

        // One InMemoryBackupPayloadProvider per BackupScope so the orchestrator
        // can resolve a provider regardless of the policy's scope until the
        // production scope-specific providers ship.
        services.AddSingleton<Cnas.Ps.Application.Backups.IBackupPayloadProvider>(
            _ => new Cnas.Ps.Infrastructure.Services.Backups.InMemoryBackupPayloadProvider(
                Cnas.Ps.Core.Domain.BackupScope.PrimaryDatabase));
        services.AddSingleton<Cnas.Ps.Application.Backups.IBackupPayloadProvider>(
            _ => new Cnas.Ps.Infrastructure.Services.Backups.InMemoryBackupPayloadProvider(
                Cnas.Ps.Core.Domain.BackupScope.FileStorage));
        services.AddSingleton<Cnas.Ps.Application.Backups.IBackupPayloadProvider>(
            _ => new Cnas.Ps.Infrastructure.Services.Backups.InMemoryBackupPayloadProvider(
                Cnas.Ps.Core.Domain.BackupScope.Logs));
        services.AddSingleton<Cnas.Ps.Application.Backups.IBackupPayloadProvider>(
            _ => new Cnas.Ps.Infrastructure.Services.Backups.InMemoryBackupPayloadProvider(
                Cnas.Ps.Core.Domain.BackupScope.EncryptionKeys));

        services.AddScoped<Cnas.Ps.Application.Backups.IBackupPolicyService,
            Cnas.Ps.Infrastructure.Services.Backups.BackupPolicyService>();
        services.AddScoped<Cnas.Ps.Application.Backups.IBackupOrchestrator,
            Cnas.Ps.Infrastructure.Services.Backups.BackupOrchestrator>();

        services.AddScoped<
            FluentValidation.IValidator<Cnas.Ps.Contracts.BackupPolicyCreateInputDto>,
            Cnas.Ps.Application.Validators.BackupPolicyCreateInputValidator>();
        services.AddScoped<
            FluentValidation.IValidator<Cnas.Ps.Contracts.BackupPolicyModifyInputDto>,
            Cnas.Ps.Application.Validators.BackupPolicyModifyInputValidator>();
        services.AddScoped<
            FluentValidation.IValidator<Cnas.Ps.Contracts.BackupPolicyReasonInputDto>,
            Cnas.Ps.Application.Validators.BackupPolicyReasonInputValidator>();
        services.AddScoped<
            FluentValidation.IValidator<Cnas.Ps.Contracts.BackupRunFilterDto>,
            Cnas.Ps.Application.Validators.BackupRunFilterValidator>();
        services.AddScoped<
            FluentValidation.IValidator<Cnas.Ps.Contracts.BackupPolicyFilterDto>,
            Cnas.Ps.Application.Validators.BackupPolicyFilterValidator>();

        // R1000..R1034 / TOR §3.2-AB..AD + §3.2-Z — voucher-quota engine
        // (spa / rehabilitation / sanatorium passports) + recurrent-payment
        // scheduler (monthly state-support). Both services are Scoped because
        // they own EF write paths through ICnasDbContext.
        services.AddScoped<Cnas.Ps.Application.UseCases.IVoucherQuotaService,
            Cnas.Ps.Infrastructure.Services.VoucherQuotaService>();
        services.AddScoped<Cnas.Ps.Application.UseCases.IRecurrentPaymentSchedulerService,
            Cnas.Ps.Infrastructure.Services.RecurrentPaymentSchedulerService>();
        // R1000..R1034 / TOR §3.2-Z — callback advancer invoked from the MPay
        // callback handler when a recurrent-payment order is confirmed.
        services.AddScoped<Cnas.Ps.Application.UseCases.IRecurrentPaymentAdvancer,
            Cnas.Ps.Infrastructure.Services.RecurrentPaymentAdvancer>();

        // R0211 / TOR UI 003 — preferred-language resolver (consumed by the
        // custom RequestCultureProvider registered in the Api composition root).
        services.AddScoped<Cnas.Ps.Application.Identity.IPreferredLanguageResolver,
            Cnas.Ps.Infrastructure.Services.Identity.PreferredLanguageResolver>();

        // R0302 / TOR §2.1 — contributor source-system change-history service + validator.
        services.AddScoped<Cnas.Ps.Application.Contributors.IContributorSourceHistoryService,
            Cnas.Ps.Infrastructure.Services.Contributors.ContributorSourceHistoryService>();
        services.AddScoped<
            FluentValidation.IValidator<Cnas.Ps.Application.Validators.ContributorSourceChangeArgs>,
            Cnas.Ps.Application.Validators.ContributorSourceChangeArgsValidator>();
        services.AddScoped<
            FluentValidation.IValidator<Cnas.Ps.Application.Validators.ContributorSourceHistoryQueryDto>,
            Cnas.Ps.Application.Validators.ContributorSourceHistoryQueryDtoValidator>();

        // R0322 / TOR UI 014 — application-attachment metadata service + validators.
        services.AddScoped<Cnas.Ps.Application.Applications.IApplicationAttachmentService,
            Cnas.Ps.Infrastructure.Services.Applications.ApplicationAttachmentService>();
        services.AddScoped<
            FluentValidation.IValidator<Cnas.Ps.Contracts.ApplicationAttachInputDto>,
            Cnas.Ps.Application.Validators.ApplicationAttachInputDtoValidator>();
        services.AddScoped<
            FluentValidation.IValidator<Cnas.Ps.Contracts.ApplicationAttachmentReasonInputDto>,
            Cnas.Ps.Application.Validators.ApplicationAttachmentReasonInputDtoValidator>();
        services.AddScoped<
            FluentValidation.IValidator<Cnas.Ps.Contracts.ApplicationAttachmentScanResultInputDto>,
            Cnas.Ps.Application.Validators.ApplicationAttachmentScanResultInputDtoValidator>();
        services.AddScoped<
            FluentValidation.IValidator<Cnas.Ps.Contracts.ApplicationAttachmentFilterDto>,
            Cnas.Ps.Application.Validators.ApplicationAttachmentFilterDtoValidator>();
        services.AddScoped<
            FluentValidation.IValidator<Cnas.Ps.Contracts.ProfileLanguageInputDto>,
            Cnas.Ps.Application.Validators.ProfileLanguageInputValidator>();

        // R0200 / TOR CF 20.01-03, MR 012 — admin cron-schedule editor service +
        // cron-expression validator. Scoped because the service captures the per-request
        // DbContext, caller context, and audit façade.
        services.AddScoped<Cnas.Ps.Application.Scheduling.ICronAdminService,
            Cnas.Ps.Infrastructure.Services.Scheduling.CronAdminService>();
        services.AddScoped<
            FluentValidation.IValidator<Cnas.Ps.Contracts.CronExpressionInputDto>,
            Cnas.Ps.Application.Validators.CronExpressionInputValidator>();

        // R0196 / TOR CF 23.02 — audit-category registry service + validators.
        services.AddScoped<Cnas.Ps.Application.Audit.IAuditCategoryService,
            Cnas.Ps.Infrastructure.Services.Audit.AuditCategoryService>();
        services.AddScoped<
            FluentValidation.IValidator<Cnas.Ps.Contracts.AuditCategoryCreateInputDto>,
            Cnas.Ps.Application.Validators.AuditCategoryCreateInputValidator>();
        services.AddScoped<
            FluentValidation.IValidator<Cnas.Ps.Contracts.AuditCategoryModifyInputDto>,
            Cnas.Ps.Application.Validators.AuditCategoryModifyInputValidator>();
        services.AddScoped<
            FluentValidation.IValidator<Cnas.Ps.Contracts.AuditCategoryReasonInputDto>,
            Cnas.Ps.Application.Validators.AuditCategoryReasonInputValidator>();
        services.AddScoped<
            FluentValidation.IValidator<Cnas.Ps.Contracts.AuditCategoryFilterDto>,
            Cnas.Ps.Application.Validators.AuditCategoryFilterValidator>();

        // R0184 / TOR SEC 042 — universal SaveChanges interceptor for auto-audit.
        // Scoped to match the IAuditService / ICallerContext lifetime; wired into
        // the DbContextOptions in DataPersistenceServiceCollectionExtensions.
        services.AddScoped<Cnas.Ps.Infrastructure.Persistence.Interceptors.AuditingInterceptor>();

        // R0191 / TOR SEC 050 / TOR ARH 028 — universal SaveChanges interceptor for
        // application-level entity-history snapshots. Scoped to match the
        // ICnasTimeProvider / ICallerContext lifetime; wired into the
        // DbContextOptions in DataPersistenceServiceCollectionExtensions.
        services.AddScoped<Cnas.Ps.Infrastructure.Persistence.Interceptors.HistoryTrackingInterceptor>();

        // R0191 / TOR SEC 050 / TOR ARH 028 — admin REST surface read façade.
        services.AddScoped<Cnas.Ps.Application.Audit.IEntityHistoryService,
            Cnas.Ps.Infrastructure.Services.Audit.EntityHistoryService>();

        // R0507 / TOR CF 01.10 — self-issued CAPTCHA challenge service + policy
        // evaluator. The in-memory store is Singleton so a challenge issued by
        // one request can be verified by the next; the policy evaluator is pure
        // and stateless.
        services.AddSingleton<Cnas.Ps.Application.Captcha.ICaptchaChallengeService,
            Cnas.Ps.Infrastructure.Security.InMemoryCaptchaChallengeService>();
        services.AddSingleton<Cnas.Ps.Application.Captcha.ICaptchaPolicyEvaluator,
            Cnas.Ps.Application.Captcha.DefaultCaptchaPolicyEvaluator>();

        // R2500 / TOR PIR 020-023 — helpdesk services + validators.
        services.AddScoped<Cnas.Ps.Application.Helpdesk.ISupportTicketCategoryService,
            Cnas.Ps.Infrastructure.Services.Helpdesk.SupportTicketCategoryService>();
        services.AddScoped<Cnas.Ps.Application.Helpdesk.ISupportTicketService,
            Cnas.Ps.Infrastructure.Services.Helpdesk.SupportTicketService>();
        services.AddScoped<Cnas.Ps.Application.Helpdesk.ISupportTicketSlaEvaluator,
            Cnas.Ps.Infrastructure.Services.Helpdesk.SupportTicketSlaEvaluator>();

        services.AddScoped<
            FluentValidation.IValidator<Cnas.Ps.Contracts.SupportTicketCategoryCreateInputDto>,
            Cnas.Ps.Application.Validators.SupportTicketCategoryCreateInputValidator>();
        services.AddScoped<
            FluentValidation.IValidator<Cnas.Ps.Contracts.SupportTicketCategoryModifyInputDto>,
            Cnas.Ps.Application.Validators.SupportTicketCategoryModifyInputValidator>();
        services.AddScoped<
            FluentValidation.IValidator<Cnas.Ps.Contracts.SupportTicketCategoryReasonInputDto>,
            Cnas.Ps.Application.Validators.SupportTicketCategoryReasonInputValidator>();
        services.AddScoped<
            FluentValidation.IValidator<Cnas.Ps.Contracts.SupportTicketCategoryFilterDto>,
            Cnas.Ps.Application.Validators.SupportTicketCategoryFilterValidator>();
        services.AddScoped<
            FluentValidation.IValidator<Cnas.Ps.Contracts.SupportTicketSubmitInputDto>,
            Cnas.Ps.Application.Validators.SupportTicketSubmitInputValidator>();
        services.AddScoped<
            FluentValidation.IValidator<Cnas.Ps.Contracts.SupportTicketAssignInputDto>,
            Cnas.Ps.Application.Validators.SupportTicketAssignInputValidator>();
        services.AddScoped<
            FluentValidation.IValidator<Cnas.Ps.Contracts.SupportTicketResolutionInputDto>,
            Cnas.Ps.Application.Validators.SupportTicketResolutionInputValidator>();
        services.AddScoped<
            FluentValidation.IValidator<Cnas.Ps.Contracts.SupportTicketReasonInputDto>,
            Cnas.Ps.Application.Validators.SupportTicketReasonInputValidator>();
        services.AddScoped<
            FluentValidation.IValidator<Cnas.Ps.Contracts.SupportTicketCommentInputDto>,
            Cnas.Ps.Application.Validators.SupportTicketCommentInputValidator>();
        services.AddScoped<
            FluentValidation.IValidator<Cnas.Ps.Contracts.SupportTicketFilterDto>,
            Cnas.Ps.Application.Validators.SupportTicketFilterValidator>();

        // R2501-R2504 / TOR PIR 022-025 — service-management quartet.
        services.AddScoped<Cnas.Ps.Application.ServiceManagement.IBusinessHoursPolicyService,
            Cnas.Ps.Infrastructure.Services.ServiceManagement.BusinessHoursPolicyService>();
        services.AddScoped<Cnas.Ps.Application.ServiceManagement.IMaintenanceWindowService,
            Cnas.Ps.Infrastructure.Services.ServiceManagement.MaintenanceWindowService>();
        services.AddScoped<Cnas.Ps.Application.ServiceManagement.ISystemUpdateScheduleService,
            Cnas.Ps.Infrastructure.Services.ServiceManagement.SystemUpdateScheduleService>();
        services.AddScoped<Cnas.Ps.Application.ServiceManagement.ISystemUpdateEventService,
            Cnas.Ps.Infrastructure.Services.ServiceManagement.SystemUpdateEventService>();

        services.AddScoped<
            FluentValidation.IValidator<Cnas.Ps.Contracts.BusinessHoursPolicyCreateInputDto>,
            Cnas.Ps.Application.Validators.BusinessHoursPolicyCreateInputValidator>();
        services.AddScoped<
            FluentValidation.IValidator<Cnas.Ps.Contracts.BusinessHoursPolicyModifyInputDto>,
            Cnas.Ps.Application.Validators.BusinessHoursPolicyModifyInputValidator>();
        services.AddScoped<
            FluentValidation.IValidator<Cnas.Ps.Contracts.BusinessHoursPolicyReasonInputDto>,
            Cnas.Ps.Application.Validators.BusinessHoursPolicyReasonInputValidator>();
        services.AddScoped<
            FluentValidation.IValidator<Cnas.Ps.Contracts.BusinessHoursPolicyFilterDto>,
            Cnas.Ps.Application.Validators.BusinessHoursPolicyFilterValidator>();
        services.AddScoped<
            FluentValidation.IValidator<Cnas.Ps.Contracts.MaintenanceWindowCreateInputDto>,
            Cnas.Ps.Application.Validators.MaintenanceWindowCreateInputValidator>();
        services.AddScoped<
            FluentValidation.IValidator<Cnas.Ps.Contracts.MaintenanceWindowReasonInputDto>,
            Cnas.Ps.Application.Validators.MaintenanceWindowReasonInputValidator>();
        services.AddScoped<
            FluentValidation.IValidator<Cnas.Ps.Contracts.MaintenanceWindowFilterDto>,
            Cnas.Ps.Application.Validators.MaintenanceWindowFilterValidator>();
        services.AddScoped<
            FluentValidation.IValidator<Cnas.Ps.Contracts.SystemUpdateScheduleCreateInputDto>,
            Cnas.Ps.Application.Validators.SystemUpdateScheduleCreateInputValidator>();
        services.AddScoped<
            FluentValidation.IValidator<Cnas.Ps.Contracts.SystemUpdateScheduleModifyInputDto>,
            Cnas.Ps.Application.Validators.SystemUpdateScheduleModifyInputValidator>();
        services.AddScoped<
            FluentValidation.IValidator<Cnas.Ps.Contracts.SystemUpdateScheduleReasonInputDto>,
            Cnas.Ps.Application.Validators.SystemUpdateScheduleReasonInputValidator>();
        services.AddScoped<
            FluentValidation.IValidator<Cnas.Ps.Contracts.SystemUpdateScheduleFilterDto>,
            Cnas.Ps.Application.Validators.SystemUpdateScheduleFilterValidator>();
        services.AddScoped<
            FluentValidation.IValidator<Cnas.Ps.Contracts.SystemUpdateEventCreateInputDto>,
            Cnas.Ps.Application.Validators.SystemUpdateEventCreateInputValidator>();
        services.AddScoped<
            FluentValidation.IValidator<Cnas.Ps.Contracts.SystemUpdateEventReasonInputDto>,
            Cnas.Ps.Application.Validators.SystemUpdateEventReasonInputValidator>();
        services.AddScoped<
            FluentValidation.IValidator<Cnas.Ps.Contracts.SystemUpdateEventFilterDto>,
            Cnas.Ps.Application.Validators.SystemUpdateEventFilterValidator>();

        // R2505 / TOR PIR 030-033 — change-management aggregate + validators.
        services.AddScoped<Cnas.Ps.Application.ServiceManagement.IChangeRequestService,
            Cnas.Ps.Infrastructure.Services.ServiceManagement.ChangeRequestService>();
        services.AddScoped<
            FluentValidation.IValidator<Cnas.Ps.Contracts.ChangeRequestCreateInputDto>,
            Cnas.Ps.Application.Validators.ChangeRequestCreateInputValidator>();
        services.AddScoped<
            FluentValidation.IValidator<Cnas.Ps.Contracts.ChangeRequestTestValidationInputDto>,
            Cnas.Ps.Application.Validators.ChangeRequestTestValidationInputValidator>();
        services.AddScoped<
            FluentValidation.IValidator<Cnas.Ps.Contracts.ChangeRequestSignCodeInputDto>,
            Cnas.Ps.Application.Validators.ChangeRequestSignCodeInputValidator>();
        services.AddScoped<
            FluentValidation.IValidator<Cnas.Ps.Contracts.ChangeRequestRollbackInputDto>,
            Cnas.Ps.Application.Validators.ChangeRequestRollbackInputValidator>();
        services.AddScoped<
            FluentValidation.IValidator<Cnas.Ps.Contracts.ChangeRequestReasonInputDto>,
            Cnas.Ps.Application.Validators.ChangeRequestReasonInputValidator>();
        services.AddScoped<
            FluentValidation.IValidator<Cnas.Ps.Contracts.ChangeRequestFilterDto>,
            Cnas.Ps.Application.Validators.ChangeRequestFilterValidator>();

        // R2506 / TOR PIR 037-040 — quality-risk registry + validators.
        services.AddScoped<Cnas.Ps.Application.ServiceManagement.IQualityRiskService,
            Cnas.Ps.Infrastructure.Services.ServiceManagement.QualityRiskService>();
        services.AddScoped<
            FluentValidation.IValidator<Cnas.Ps.Contracts.QualityRiskCreateInputDto>,
            Cnas.Ps.Application.Validators.QualityRiskCreateInputValidator>();
        services.AddScoped<
            FluentValidation.IValidator<Cnas.Ps.Contracts.QualityRiskModifyInputDto>,
            Cnas.Ps.Application.Validators.QualityRiskModifyInputValidator>();
        services.AddScoped<
            FluentValidation.IValidator<Cnas.Ps.Contracts.QualityRiskReviewInputDto>,
            Cnas.Ps.Application.Validators.QualityRiskReviewInputValidator>();
        services.AddScoped<
            FluentValidation.IValidator<Cnas.Ps.Contracts.QualityRiskReasonInputDto>,
            Cnas.Ps.Application.Validators.QualityRiskReasonInputValidator>();
        services.AddScoped<
            FluentValidation.IValidator<Cnas.Ps.Contracts.QualityRiskFilterDto>,
            Cnas.Ps.Application.Validators.QualityRiskFilterValidator>();
        services.AddScoped<
            FluentValidation.IValidator<Cnas.Ps.Contracts.QualityRiskActionCreateInputDto>,
            Cnas.Ps.Application.Validators.QualityRiskActionCreateInputValidator>();
        services.AddScoped<
            FluentValidation.IValidator<Cnas.Ps.Contracts.QualityRiskActionModifyInputDto>,
            Cnas.Ps.Application.Validators.QualityRiskActionModifyInputValidator>();
        services.AddScoped<
            FluentValidation.IValidator<Cnas.Ps.Contracts.QualityRiskActionImplementInputDto>,
            Cnas.Ps.Application.Validators.QualityRiskActionImplementInputValidator>();
        services.AddScoped<
            FluentValidation.IValidator<Cnas.Ps.Contracts.QualityRiskActionReasonInputDto>,
            Cnas.Ps.Application.Validators.QualityRiskActionReasonInputValidator>();

        services.AddCnasJobs();

        return services;
    }

    /// <summary>
    /// R1503 / TOR §3.7-D — registers a concrete
    /// <see cref="Cnas.Ps.Application.Recalculation.IBenefitRecalculationStrategy"/>
    /// for the mass-recalculation engine. Strategies are resolved by the
    /// orchestrator as
    /// <see cref="System.Collections.Generic.IEnumerable{T}"/>; the
    /// orchestrator dispatches by <c>BenefitType</c>. Registering more than
    /// one strategy for the same <c>BenefitType</c> is a startup error
    /// surfaced by the orchestrator at run time.
    /// </summary>
    /// <typeparam name="TStrategy">Concrete strategy CLR type.</typeparam>
    /// <param name="services">Service collection to mutate.</param>
    /// <returns>The same <paramref name="services"/> for fluent chaining.</returns>
    public static IServiceCollection AddBenefitRecalculationStrategy<TStrategy>(
        this IServiceCollection services)
        where TStrategy : class, Cnas.Ps.Application.Recalculation.IBenefitRecalculationStrategy
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<Cnas.Ps.Application.Recalculation.IBenefitRecalculationStrategy, TStrategy>();
        return services;
    }

    /// <summary>
    /// R2273 / TOR SEC 027 — registers a concrete
    /// <see cref="Cnas.Ps.Application.SensitiveActions.ISensitiveActionPolicy"/> with
    /// the generic 4-eyes substrate. Future iterations call this once per concrete
    /// sensitive action (USER.ROLE_GRANT, EXECUTORY_DOC.CANCEL, …).
    /// </summary>
    /// <typeparam name="TPolicy">The policy implementation type.</typeparam>
    /// <param name="services">Service collection to mutate.</param>
    /// <returns>The same <paramref name="services"/> for fluent chaining.</returns>
    public static IServiceCollection AddSensitiveActionPolicy<TPolicy>(
        this IServiceCollection services)
        where TPolicy : class, Cnas.Ps.Application.SensitiveActions.ISensitiveActionPolicy
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<Cnas.Ps.Application.SensitiveActions.ISensitiveActionPolicy, TPolicy>();
        return services;
    }

    /// <summary>
    /// R2273 / TOR SEC 027 — registers a concrete
    /// <see cref="Cnas.Ps.Application.SensitiveActions.ISensitiveActionHandler"/> with
    /// the generic 4-eyes substrate.
    /// </summary>
    /// <typeparam name="THandler">The handler implementation type.</typeparam>
    /// <param name="services">Service collection to mutate.</param>
    /// <returns>The same <paramref name="services"/> for fluent chaining.</returns>
    public static IServiceCollection AddSensitiveActionHandler<THandler>(
        this IServiceCollection services)
        where THandler : class, Cnas.Ps.Application.SensitiveActions.ISensitiveActionHandler
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<Cnas.Ps.Application.SensitiveActions.ISensitiveActionHandler, THandler>();
        return services;
    }

    /// <summary>
    /// R1810 / TOR BP 1.2-I — swaps the default in-memory Treasury feed source
    /// for the HTTPS placeholder. Production deployments call this from their
    /// composition root once <c>TreasuryFeedOptions.HttpsBaseUrl</c> is wired.
    /// Tests continue to use the in-memory default for hermetic runs.
    /// </summary>
    /// <param name="services">Service collection to mutate.</param>
    /// <returns>The same <paramref name="services"/> for fluent chaining.</returns>
    public static IServiceCollection UseHttpsTreasuryFeedSource(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        // The original singleton registration is overwritten by the last call
        // to AddSingleton<TService, TImpl>(...) per ServiceCollection semantics.
        services.AddSingleton<Cnas.Ps.Application.Treasury.Feed.ITreasuryFeedSource,
            Cnas.Ps.Infrastructure.Services.Treasury.Feed.HttpsTreasuryFeedSource>();
        return services;
    }

    /// <summary>
    /// Registers the <see cref="ISecretsProvider"/> abstraction with one of two
    /// backends, selected by the <c>Cnas:Secrets:Provider</c> configuration key:
    /// <list type="bullet">
    ///   <item><c>"Environment"</c> (default) — <see cref="EnvironmentSecretsProvider"/> reads process environment variables.</item>
    ///   <item><c>"Vault"</c> — <see cref="VaultSecretsProvider"/> reads HashiCorp Vault KV v2 over HTTPS.</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// <para>
    /// The Vault backend binds <see cref="VaultSecretsOptions"/> from
    /// <c>Cnas:Secrets:Vault</c> and registers a typed <see cref="HttpClient"/> with
    /// a 30-second timeout matching the rest of the MGov outbound budget.
    /// </para>
    /// <para>
    /// The Environment backend is registered as a singleton because the underlying
    /// API (<see cref="Environment.GetEnvironmentVariable(string)"/>) is stateless
    /// and thread-safe; the Vault backend's lifetime is managed by
    /// <see cref="IHttpClientFactory"/>.
    /// </para>
    /// <para>
    /// Composition root should call this method exactly once, alongside
    /// <see cref="AddCnasInfrastructure"/>. It is intentionally separate so that
    /// integration tests that use only the env-var provider don't have to spin up
    /// the rest of the infrastructure graph.
    /// </para>
    /// </remarks>
    /// <param name="services">Service collection to mutate.</param>
    /// <param name="configuration">Application configuration root.</param>
    /// <returns>The same <paramref name="services"/> instance for fluent chaining.</returns>
    public static IServiceCollection AddCnasSecrets(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var providerName = configuration["Cnas:Secrets:Provider"] ?? "Environment";

        if (string.Equals(providerName, "Vault", StringComparison.OrdinalIgnoreCase))
        {
            services.AddOptions<VaultSecretsOptions>()
                .Bind(configuration.GetSection(VaultSecretsOptions.SectionName))
                .Validate(o => !string.IsNullOrWhiteSpace(o.BaseUrl),
                    "Cnas:Secrets:Vault:BaseUrl is required when provider is 'Vault'.")
                .ValidateOnStart();

            services.AddHttpClient<ISecretsProvider, VaultSecretsProvider>()
                .ConfigureHttpClient((_, c) =>
                {
                    c.Timeout = TimeSpan.FromSeconds(30);
                    c.DefaultRequestHeaders.UserAgent.ParseAdd("CNAS-PS/1.0");
                });
        }
        else
        {
            services.AddSingleton<ISecretsProvider, EnvironmentSecretsProvider>();
        }

        return services;
    }

    /// <summary>
    /// Registers the universal mTLS / client-certificate foundation for MGov adapters.
    /// Binds <see cref="MTlsOptions"/> from <c>Cnas:MGov:Mtls</c> and registers
    /// <see cref="ICertificateStore"/> (implementation: <see cref="FileCertificateStore"/>)
    /// as a singleton.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This method is purely foundational: it does not alter any existing
    /// <see cref="HttpClient"/> registration. Per-service adapters (MNotify, MLog,
    /// MSign, ...) will opt into mTLS in subsequent refactor rounds by wiring
    /// <see cref="ClientCertificateHttpHandler"/> as a delegating handler and
    /// configuring the primary <see cref="System.Net.Http.SocketsHttpHandler"/> with
    /// the certificate resolved from the store. Until that wiring lands, the
    /// existing Bearer-token flow remains the active authentication path — see
    /// <c>docs/EGOV-INTEGRATION-GAP.md</c> §"mTLS / Client Certificates".
    /// </para>
    /// <para>
    /// Idempotent: safe to call alongside <see cref="AddCnasInfrastructure"/> at the
    /// composition root.
    /// </para>
    /// </remarks>
    /// <param name="services">Service collection to mutate.</param>
    /// <param name="configuration">
    /// Root configuration; <c>Cnas:MGov:Mtls</c> is bound to <see cref="MTlsOptions"/>.
    /// </param>
    /// <returns>The same <paramref name="services"/> instance for fluent chaining.</returns>
    public static IServiceCollection AddCnasMTls(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Bind via Configure-callback so we can iterate IConfiguration's children
        // explicitly. The default ConfigurationBinder will not populate the
        // case-insensitive Dictionary<string, MTlsCertificateOptions> because positional
        // records' init-only constructor parameters don't round-trip cleanly through the
        // binder in every .NET 10 configuration provider — explicit iteration is robust.
        services.Configure<MTlsOptions>(opts =>
        {
            var certs = configuration.GetSection($"{MTlsOptions.SectionName}:Certificates");
            foreach (var child in certs.GetChildren())
            {
                var path = child["Path"];
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }
                var password = child["Password"];
                var thumbprint = child["Thumbprint"];
                opts.Certificates[child.Key] = new MTlsCertificateOptions(path, password, thumbprint);
            }
        });

        services.AddSingleton<ICertificateStore, FileCertificateStore>();

        return services;
    }

    /// <summary>
    /// Registers the MCabinet citizen-portal publisher. Bound from <c>Cnas:MCabinet</c>
    /// and wired as a typed <see cref="HttpClient"/> with a 30-second timeout and the
    /// shared <c>CNAS-PS/1.0</c> user-agent so AGE-side audit dashboards continue to
    /// attribute the traffic to CNAS-PS.
    /// </summary>
    /// <remarks>
    /// Kept as a separate registration call (rather than folded into
    /// <see cref="AddCnasInfrastructure"/>) because MCabinet is opt-in per environment —
    /// staging and CI deliberately leave <see cref="MCabinetOptions.BaseUrl"/> empty so
    /// the publisher short-circuits to <see cref="ErrorCodes.MCabinetPublishFailed"/>
    /// rather than touching the production citizen portal.
    /// </remarks>
    /// <param name="services">Service collection to mutate.</param>
    /// <param name="configuration">Root configuration; <c>Cnas:MCabinet</c> is bound to <see cref="MCabinetOptions"/>.</param>
    /// <returns>The same <paramref name="services"/> instance for fluent chaining.</returns>
    public static IServiceCollection AddCnasMCabinet(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<MCabinetOptions>()
            .Bind(configuration.GetSection(MCabinetOptions.SectionName))
            .ValidateOnStart();

        // MCabinet uses mTLS via the shared primary-handler factory — the certificate is
        // registered under Cnas:MGov:Mtls:Certificates:mcabinet. When no certificate is
        // configured the handler still uses SocketsHttpHandler (so a cert can be added
        // without re-registration) but with an empty ClientCertificates list, falling
        // back to a Bearer-less HTTPS handshake — used by dev/CI and by environments
        // that have not yet been migrated off the Bearer-token model.
        services.AddHttpClient<IMCabinetPublisher, MCabinetPublisher>(nameof(MCabinetPublisher))
            .ConfigureHttpClient((_, c) =>
            {
                c.Timeout = TimeSpan.FromSeconds(30);
                c.DefaultRequestHeaders.UserAgent.ParseAdd("CNAS-PS/1.0");
            })
            .ConfigurePrimaryHttpMessageHandler(sp => BuildMGovPrimaryHandler(sp, "mcabinet"))
            .AddMGovResilience("mcabinet");

        return services;
    }

    /// <summary>
    /// R0117 / CF 14.11 — registers the Portalul guvernamental de date (PGD) publisher.
    /// Bound from <c>Cnas:Pgd</c>. The publisher is opt-in per environment: when
    /// <c>BaseUrl</c> is blank the call short-circuits to a deterministic
    /// <see cref="ErrorCodes.PgdNotConfigured"/> failure without touching the network.
    /// </summary>
    /// <param name="services">Service collection to mutate.</param>
    /// <param name="configuration">Root configuration containing <c>Cnas:Pgd</c>.</param>
    /// <returns>The same <paramref name="services"/> instance for fluent chaining.</returns>
    public static IServiceCollection AddCnasPgdPublisher(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<Cnas.Ps.Infrastructure.Services.MessageBus.PgdPublisherOptions>()
            .Bind(configuration.GetSection(Cnas.Ps.Infrastructure.Services.MessageBus.PgdPublisherOptions.SectionName));

        services.AddHttpClient<
                Cnas.Ps.Application.MessageBus.IPgdPublisher,
                Cnas.Ps.Infrastructure.Services.MessageBus.PgdPublisher>(
                nameof(Cnas.Ps.Infrastructure.Services.MessageBus.PgdPublisher))
            .ConfigureHttpClient((_, c) =>
            {
                c.Timeout = TimeSpan.FromSeconds(30);
                c.DefaultRequestHeaders.UserAgent.ParseAdd("CNAS-PS/1.0");
            });

        return services;
    }

    /// <summary>
    /// Registers the MPass SAML assertion-parsing foundation. Binds
    /// <see cref="MPassSamlOptions"/> from <c>Cnas:MGov:MPassSaml</c> and registers
    /// <see cref="ISamlAssertionParser"/> -&gt; <see cref="MPassSamlAssertionParser"/>
    /// as a singleton (stateless, thread-safe).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This method is intentionally opt-in: it is NOT invoked from
    /// <see cref="AddCnasInfrastructure"/>. The API host wires it explicitly once the
    /// MEGA staging certificate is provisioned and the live OIDC -&gt; SAML middleware
    /// swap is performed (see <c>docs/EGOV-INTEGRATION-GAP.md</c> §MPass). Until then
    /// the parser is exercised only by the prep-phase ACS controller, which is
    /// resolved on demand by feature tests and operator dry-runs.
    /// </para>
    /// <para>
    /// Idempotent: safe to call alongside <see cref="AddCnasInfrastructure"/> at the
    /// composition root.
    /// </para>
    /// </remarks>
    /// <param name="services">Service collection to mutate.</param>
    /// <param name="configuration">
    /// Root configuration; <c>Cnas:MGov:MPassSaml</c> is bound to
    /// <see cref="MPassSamlOptions"/>.
    /// </param>
    /// <returns>The same <paramref name="services"/> instance for fluent chaining.</returns>
    public static IServiceCollection AddCnasMPassSaml(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<MPassSamlOptions>()
            .Bind(configuration.GetSection(MPassSamlOptions.SectionName))
            .Validate(o =>
                !IsProductionEnvironment(configuration) || !o.AllowUnsignedAssertionsForTesting,
                "Cnas:MGov:MPassSaml:AllowUnsignedAssertionsForTesting must be false in Production.")
            .ValidateOnStart();

        services.AddSingleton<ISamlAssertionParser, MPassSamlAssertionParser>();

        return services;
    }

    /// <summary>
    /// Outbound HTTP defaults applied to every MGov typed-client. A uniform timeout and
    /// user-agent give operations dashboards a single ceiling for upstream-related latency
    /// and let AGE-side audit logs attribute every request to the CNAS-PS subsystem.
    /// </summary>
    /// <remarks>
    /// Moved out of <see cref="AddCnasInfrastructure"/> into a file-scoped private helper so
    /// the MCabinet registration (which lives in <see cref="AddCnasMCabinet"/>) and any
    /// future per-service extension method can share the same configuration without
    /// duplicating the logic.
    /// </remarks>
    /// <param name="_">Service provider (unused — kept to match the <see cref="IHttpClientBuilder"/> overload).</param>
    /// <param name="client">The <see cref="HttpClient"/> to mutate in place.</param>
    private static void ConfigureMGovHttp(IServiceProvider _, HttpClient client)
    {
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CNAS-PS/1.0");
    }

    /// <summary>
    /// Shared primary-handler factory used by every MGov client opting into mTLS. The
    /// <paramref name="serviceName"/> matches the stable key in
    /// <see cref="MTlsOptions.Certificates"/>. When no cert is configured the handler is
    /// still a <see cref="SocketsHttpHandler"/> (a future cert can be added without
    /// re-registration) but with an empty <c>ClientCertificates</c> list, falling back to
    /// a Bearer-less HTTPS handshake — the path used by dev/CI and by environments that
    /// have not yet been migrated off the Bearer-token model.
    /// </summary>
    /// <remarks>
    /// Moved out of <see cref="AddCnasInfrastructure"/> into a file-scoped private helper
    /// so <see cref="AddCnasMCabinet"/> can call it. The body is identical to the previous
    /// local function (single source of truth — no duplication).
    /// </remarks>
    /// <param name="sp">Service provider used to resolve <see cref="MTlsOptions"/> and <see cref="ICertificateStore"/>.</param>
    /// <param name="serviceName">Stable per-service key matching <see cref="MTlsOptions.Certificates"/>.</param>
    /// <returns>A <see cref="SocketsHttpHandler"/> wired with the resolved client certificate (if any).</returns>
    private static System.Net.Http.SocketsHttpHandler BuildMGovPrimaryHandler(IServiceProvider sp, string serviceName)
    {
        var handler = new System.Net.Http.SocketsHttpHandler();
        var ssl = new System.Net.Security.SslClientAuthenticationOptions
        {
            ClientCertificates = new System.Security.Cryptography.X509Certificates.X509CertificateCollection(),
        };
        // Resolve the certificate first via the options snapshot directly (more robust
        // than going through ICertificateStore — the latter is bypassed in tests where
        // AddCnasMTls was not invoked). If that fails, fall through to the store as a
        // backup path for any registration nuance we haven't covered.
        var opts = sp.GetService<Microsoft.Extensions.Options.IOptions<MTlsOptions>>()?.Value;
        if (opts is not null && opts.Certificates.TryGetValue(serviceName, out var entry))
        {
            try
            {
                var direct = string.IsNullOrEmpty(entry.Password)
                    ? System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12FromFile(entry.Path, password: null)
                    : System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12FromFile(entry.Path, password: entry.Password);
                ssl.ClientCertificates.Add(direct);
            }
            catch
            {
                // Cert load failed — fall through with an empty list (Bearer fallback).
            }
        }
        else
        {
            var store = sp.GetService<ICertificateStore>();
            if (store is not null)
            {
                var probe = store.TryGetCertificate(serviceName);
                if (probe.IsSuccess && probe.Value is not null)
                {
                    ssl.ClientCertificates.Add(probe.Value);
                }
            }
        }
        handler.SslOptions = ssl;
        return handler;
    }

    /// <summary>
    /// Validates that <paramref name="signingKey"/> is a non-empty base64 string that
    /// decodes to at least 32 bytes — the HS256 minimum per RFC 7518 §3.2 (the key
    /// length must be ≥ hash output length, which is 256 bits = 32 bytes for SHA-256).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Used by the <see cref="JwtOptions"/> startup validator so a misconfigured
    /// signing key fails the host build rather than producing silently-weak signatures
    /// at request time. A malformed base64 string also fails (FromBase64String throws
    /// FormatException), which we map to "false" so the validator returns the
    /// canonical error message.
    /// </para>
    /// </remarks>
    /// <param name="signingKey">Candidate base64-encoded signing key.</param>
    /// <returns><c>true</c> iff the key is valid base64 and ≥ 32 bytes after decoding.</returns>
    private static bool IsValidSigningKey(string? signingKey)
    {
        if (string.IsNullOrWhiteSpace(signingKey))
        {
            return false;
        }
        try
        {
            return Convert.FromBase64String(signingKey).Length >= 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool IsProductionEnvironment(IConfiguration configuration)
    {
        var environment = configuration["ASPNETCORE_ENVIRONMENT"]
            ?? configuration["DOTNET_ENVIRONMENT"];
        return string.Equals(environment, "Production", StringComparison.OrdinalIgnoreCase);
    }
}
