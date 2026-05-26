using System.Linq;
using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.E2E.Tests.Auth;
using Cnas.Ps.Infrastructure;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Cnas.Ps.E2E.Tests;

/// <summary>
/// xUnit collection fixture that boots the <c>Cnas.Ps.Api</c> composition root inside
/// the test process on a real Kestrel port so Playwright can drive it over HTTP.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{TEntryPoint}"/>
/// is intentionally NOT used here because its default <c>Server</c> is an in-memory
/// <c>TestServer</c> that does not bind to a TCP port. Mixing TestServer and Kestrel
/// in the same factory throws <c>InvalidCastException</c> when the base class tries
/// to retrieve <c>Server</c>. For E2E we want a genuine HTTP listener, so we build a
/// <see cref="WebApplication"/> ourselves via the same <c>AddCnasApi</c> /
/// <c>UseCnasApiPipeline</c> composition that <c>Program.cs</c> uses in production.
/// </para>
/// <para>
/// Production dependencies that the E2E tests have no business touching are stubbed
/// at registration time:
/// <list type="bullet">
///   <item><c>Cnas:SkipMigrations</c> is set to <c>true</c> so the host does not try
///         to migrate PostgreSQL on startup.</item>
///   <item>The Npgsql-backed <see cref="CnasDbContext"/> registration is replaced
///         with an EF Core in-memory database (unique per fixture instance).</item>
///   <item>The Postgres connection string is set to a localhost dummy so the
///         NpgSql health-check registration does not throw during DI build.</item>
///   <item>MGov base URLs are left empty (the default) so MGov health checks return
///         <c>Degraded</c> without making any outbound HTTP call — same pattern the
///         existing <c>MGovHealthCheckTests</c> rely on.</item>
/// </list>
/// </para>
/// <para>
/// <b>Test-only authentication.</b> When the configuration switch
/// <c>Cnas:E2E:TestAuth:Enabled</c> is <c>true</c>, the fixture removes the production
/// cookie + OIDC scheme registrations and substitutes a header-driven test scheme that
/// reads <c>X-Test-User</c>. The substitution is opt-in so the original three journey
/// tests (which leave the switch off) keep exercising the real authentication
/// composition. See <see cref="AuthenticatedApiHostFixture"/> for the opted-in variant.
/// </para>
/// </remarks>
public class ApiHostFixture : IAsyncLifetime
{
    private WebApplication? _app;

    /// <summary>Base address of the running Kestrel host (e.g. <c>http://127.0.0.1:54321</c>).</summary>
    public string BaseAddress { get; private set; } = string.Empty;

    /// <summary>
    /// Root service provider of the running host. Tests use this to resolve scoped
    /// services (e.g. <c>CnasDbContext</c>) for seeding and post-condition assertions
    /// — see the UC06 / UC23 journeys for examples. <c>null</c> until
    /// <see cref="InitializeAsync"/> has completed.
    /// </summary>
    public IServiceProvider Services => _app?.Services
        ?? throw new InvalidOperationException("Host not started — call InitializeAsync first.");

    /// <summary>
    /// Composes the same DI graph the production API uses, replaces the EF Core
    /// PostgreSQL DbContext with an InMemory one, and starts a Kestrel listener on
    /// an OS-assigned free port. Captures the resolved URL into
    /// <see cref="BaseAddress"/>.
    /// </summary>
    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();

        builder.Environment.EnvironmentName = "Testing";

        // Inject the E2E-only configuration values BEFORE AddCnasApi reads them.
        // The base dictionary is the shared, always-applied set; subclasses extend
        // it via ConfigureAdditionalSettings to opt into encryption keys, test-auth,
        // and other behaviours required by authenticated journeys.
        var settings = new Dictionary<string, string?>
        {
            ["Cnas:SkipMigrations"] = "true",
            ["ConnectionStrings:Postgres"] = "Host=localhost;Port=5432;Database=cnas_e2e;Username=ignored;Password=ignored",
            ["Sqids:Alphabet"] = "FedcbHijklmnoGpqrstuvwxyZ0123456789ABCDEIJKLMNOPQRSTUVWXY",
            ["Sqids:MinLength"] = "8",
            // MinIO endpoint / bucket name are pinned so MinioOptions validation passes,
            // but AccessKey and SecretKey are intentionally left empty: the
            // AddCnasInfrastructure registration detects the missing credentials and
            // wires the fail-loud MissingMinioFileStorage sentinel in place of the real
            // MinioClient. The sentinel constructs cleanly (so unrelated controller
            // activations succeed) and throws InvalidOperationException only if a code
            // path actually calls IFileStorage — which no E2E journey does. This
            // mirrors the MissingKeyFieldEncryptor / MissingSaltHmacHasher pattern.
            ["Minio:Endpoint"] = "localhost:9000",
            ["Minio:UseSsl"] = "false",
            ["Minio:CitizenUploadsBucket"] = "citizen-uploads-e2e",
            ["Cnas:Secrets:Provider"] = "Environment",
            // R0035 — disable Cloudflare Turnstile verification in the E2E suite so
            // the anonymous UC01 / UC02 journeys (and any other [RequireCaptcha]-gated
            // controller) keep passing without hitting the real provider. Production
            // / staging config sets this to false; the integration test fixture is the
            // ONLY place where BypassForTesting is allowed to be true.
            ["Cnas:Captcha:Turnstile:BypassForTesting"] = "true",
            ["Cnas:Captcha:Turnstile:SecretKey"] = "test-secret-key-not-used-while-bypass",
            ["Cnas:Captcha:Turnstile:SiteKey"] = "test-site-key-not-used-while-bypass",
            // R0053 / SEC 018 — JWT pipeline. The signing-key validator at startup
            // rejects keys that decode to fewer than 32 bytes; we supply an all-zero
            // 32-byte buffer (44 chars after base64) which is sufficient for unit /
            // E2E coverage. Production reads the actual key from the secrets manager.
            ["Jwt:Issuer"] = "https://cnas.test",
            ["Jwt:Audience"] = "cnas-api",
            ["Jwt:SigningKey"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
        };
        ConfigureAdditionalSettings(settings);
        builder.Configuration.AddInMemoryCollection(settings);

        // Mirror Program.cs ordering — Serilog is wired before AddCnasApi because the
        // pipeline middleware (UseSerilogRequestLogging) resolves IDiagnosticContext
        // from DI, which AddCnasObservability registers.
        builder.AddCnasObservability();
        builder.Services.AddCnasApi(builder.Configuration);

        // MCabinet publisher — required by ApplicationServiceImpl + the workflow
        // services for citizen-portal outbound projection. Registered as a separate
        // composition entry in production so feature flags can disable it per env;
        // without this call the application-submission journeys 500 with "Unable to
        // resolve service for type 'IMCabinetPublisher'". The MCabinet base URL is
        // intentionally left empty in the E2E config so the publisher short-circuits
        // to a best-effort no-op (matching the dev/CI behaviour documented on
        // ApplicationServiceImpl.PublishMCabinetAsync).
        builder.Services.AddCnasMCabinet(builder.Configuration);

        // Defense-in-depth override: AddCnasInfrastructure decides between
        // AesFieldEncryptor and MissingKeyFieldEncryptor based on whether
        // Cnas:FieldEncryption:Key is set at registration time. To eliminate any
        // ambiguity about configuration-source ordering or option re-binding, we
        // explicitly remove every existing IFieldEncryptor / IDeterministicHasher
        // registration and re-add the AesFieldEncryptor / Hmac256Hasher implementations
        // when ConfigureAdditionalSettings populated the encryption keys. The
        // implementation types live behind public DI extensions in the production
        // composition, so we reuse them rather than re-implementing.
        var encKey = builder.Configuration["Cnas:FieldEncryption:Key"];
        var hashSalt = builder.Configuration["Cnas:FieldHashing:SaltKey"];
        if (!string.IsNullOrWhiteSpace(encKey))
        {
            builder.Services.RemoveAll<Cnas.Ps.Application.Abstractions.IFieldEncryptor>();
            builder.Services.AddSingleton<Cnas.Ps.Application.Abstractions.IFieldEncryptor,
                Cnas.Ps.Infrastructure.Security.AesFieldEncryptor>();
        }
        if (!string.IsNullOrWhiteSpace(hashSalt))
        {
            builder.Services.RemoveAll<Cnas.Ps.Application.Abstractions.IDeterministicHasher>();
            builder.Services.AddSingleton<Cnas.Ps.Application.Abstractions.IDeterministicHasher,
                Cnas.Ps.Infrastructure.Security.Hmac256Hasher>();
        }

        // MVC controller discovery looks at the entry assembly's ApplicationPartManager
        // by default. Because the host is bootstrapped from the E2E test assembly (which
        // contains no controllers), we must explicitly attach the Cnas.Ps.Api assembly
        // so its [ApiController]s — PublicController, ApplicationsController, etc. —
        // are routed by MapControllers(). Without this every /api/* request 404s even
        // though the controllers are wired and DI resolves them; the symptom looks like
        // a routing bug but is really a missing ApplicationPart.
        builder.Services.AddControllers()
            .AddApplicationPart(typeof(Cnas.Ps.Api.Composition.ApiCompositionRoot).Assembly);

        // Replace the Npgsql-backed DbContext with EF Core InMemory so the host
        // resolves without a live PostgreSQL instance. A unique database name keeps
        // parallel fixture instances (if any) isolated.
        //
        // We must scrub every EF registration AddCnasInfrastructure plants — the
        // visible DbContextOptions<CnasDbContext> entry, the IDbContextOptions hook,
        // AND every per-DbContext options-configuration registration that carries
        // the Npgsql provider services. Leaving any of them in place causes EF to
        // detect both Npgsql and InMemory providers in the same service provider and
        // throw "Only a single database provider can be registered in a service
        // provider" the first time a DbSet is dereferenced. The
        // IDbContextOptionsConfiguration&lt;T&gt; type is internal to EF, so we match
        // on its type-name prefix instead of a strongly-typed RemoveAll&lt;T&gt;.
        // We cannot use UseInternalServiceProvider as an alternative because
        // CnasDbContext.OnConfiguring calls ReplaceService (for the model-cache key
        // factory), which is incompatible with an externally-supplied EF service
        // provider.
        builder.Services.RemoveAll<DbContextOptions<CnasDbContext>>();
        // R0026 — also scrub the read-only DbContext's options so the InMemory
        // provider is the only one wired for both contexts. Both contexts must
        // share the same InMemory database name so a row written via the primary
        // (ICnasDbContext) is visible via the read-only surface (IReadOnlyCnasDbContext)
        // — replica lag does not exist in the InMemory store.
        builder.Services.RemoveAll<DbContextOptions<CnasReadOnlyDbContext>>();
        builder.Services.RemoveAll<DbContextOptions>();
        var efOptionsConfigDescriptors = builder.Services
            .Where(d => d.ServiceType.IsGenericType
                && d.ServiceType.Name.StartsWith("IDbContextOptionsConfiguration", StringComparison.Ordinal))
            .ToList();
        foreach (var d in efOptionsConfigDescriptors)
        {
            builder.Services.Remove(d);
        }
        var dbName = $"cnas-e2e-{Guid.NewGuid():N}";
        builder.Services.AddDbContext<CnasDbContext>(opts => opts.UseInMemoryDatabase(dbName));
        builder.Services.AddDbContext<CnasReadOnlyDbContext>(opts => opts.UseInMemoryDatabase(dbName));

        // Drop the NpgSql readiness probe (registered by AddCnasApi) because it would
        // actually try to open a TCP connection to the dummy "localhost:5432" string
        // and surface an unhandled exception as HTTP 500 on /health/ready. The MinIO
        // probe is removed for the same reason — it dials "localhost:9000" and throws
        // when no broker is listening. The MGov/workflow probes already short-circuit
        // to Degraded when their base URLs are empty, so they remain in place.
        builder.Services.Configure<HealthCheckServiceOptions>(opts =>
        {
            var toRemove = opts.Registrations
                .Where(r => r.Name is "db.postgres" or "storage.minio")
                .ToList();
            foreach (var r in toRemove)
            {
                opts.Registrations.Remove(r);
            }
        });

        // Opt-in: register the header-driven test scheme alongside the production
        // schemes and re-point the default authenticate/challenge scheme at it via
        // PostConfigure. Bound from Cnas:E2E:TestAuth:Enabled — defaults to false so
        // the original journey tests keep exercising the real authentication
        // composition. We deliberately avoid redefining the production cookie scheme
        // (which would throw "Scheme already exists: Cookies"); instead the test
        // scheme uses a unique name and PostConfigure rewires the defaults so
        // [Authorize] resolves the test principal without ever consulting the cookie
        // or OIDC handlers.
        var testAuthEnabled = builder.Configuration
            .GetSection(TestAuthOptions.SectionName)
            .Get<TestAuthOptions>()?.Enabled
            ?? false;
        if (testAuthEnabled)
        {
            builder.Services.AddAuthentication()
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                    TestAuthHandler.SchemeName, _ => { });

            builder.Services.PostConfigure<AuthenticationOptions>(opts =>
            {
                opts.DefaultScheme = TestAuthHandler.SchemeName;
                opts.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                opts.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                opts.DefaultSignInScheme = TestAuthHandler.SchemeName;
                opts.DefaultSignOutScheme = TestAuthHandler.SchemeName;
                opts.DefaultForbidScheme = TestAuthHandler.SchemeName;
            });
        }

        // R0186 — swap the production async IAuditService for the synchronous E2E
        // variant. The production path enqueues onto AuditWriteQueue and a hosted
        // AuditDrainer flushes asynchronously; that races every E2E assertion that
        // reads AuditLogs immediately after the HTTP call. The async pipeline is
        // independently covered by AuditDrainerTests in the Infrastructure.Tests
        // project, so dropping it from the E2E suite does not erode coverage — it
        // just keeps the journey assertions deterministic. We also remove the
        // hosted AuditDrainer so it doesn't compete for records that will never
        // be enqueued (the synchronous variant writes straight to the DbContext).
        builder.Services.RemoveAll<IAuditService>();
        builder.Services.AddScoped<IAuditService, SynchronousAuditService>();
        var auditDrainerDescriptors = builder.Services
            .Where(d => d.ImplementationType == typeof(AuditDrainer)
                || (d.ServiceType == typeof(IHostedService)
                    && d.ImplementationType == typeof(AuditDrainer)))
            .ToList();
        foreach (var d in auditDrainerDescriptors)
        {
            builder.Services.Remove(d);
        }

        // Service-graph mutation hook. Subclasses opt in to additional service
        // substitutions (e.g. UC17 phase 2A's InMemoryFileStorage replacing the
        // MissingMinioFileStorage sentinel) without re-implementing the whole boot
        // sequence. Runs AFTER every default registration so subclasses can use
        // RemoveAll to scrub a production registration before adding their substitute —
        // necessary because IFileStorage is wired as a singleton; appending without
        // removing would leave the sentinel as the resolved implementation.
        ConfigureAdditionalServices(builder.Services);

        // Bind to a dynamic free port — 0 = OS picks one.
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        _app = builder.Build();
        _app.UseCnasApiPipeline();

        await _app.StartAsync().ConfigureAwait(false);

        var server = _app.Services.GetRequiredService<IServer>();
        var addresses = server.Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel did not expose IServerAddressesFeature.");
        BaseAddress = addresses.Addresses.First().TrimEnd('/');
    }

    /// <summary>
    /// Extension point for subclasses to layer additional configuration on top of the
    /// shared E2E defaults. Called once just before <see cref="AddCnasApi"/> reads the
    /// configuration. The default implementation is a no-op so the original journey
    /// tests retain the historical settings exactly. Subclasses such as
    /// <see cref="AuthenticatedApiHostFixture"/> override this hook to enable
    /// test-auth and provision field-encryption keys without touching the production
    /// composition.
    /// </summary>
    /// <param name="settings">
    /// Mutable settings dictionary that becomes the in-memory configuration source.
    /// Subclasses add keys to this dictionary; values may be <c>null</c> to clear a
    /// previously-set entry.
    /// </param>
    protected virtual void ConfigureAdditionalSettings(IDictionary<string, string?> settings)
    {
    }

    /// <summary>
    /// Service-graph mutation hook called once just before <c>builder.Build()</c>. The
    /// default implementation is a no-op so the original journey tests retain the
    /// historical service registrations exactly. Subclasses such as
    /// <see cref="AuthenticatedApiHostFixture"/> override this hook to scrub a
    /// production registration and substitute a test-only implementation (e.g.
    /// replacing the missing-MinIO sentinel with the
    /// <see cref="Cnas.Ps.E2E.Tests.Storage.InMemoryFileStorage"/> substitute that
    /// the UC17 phase 2A upload / download journey depends on).
    /// </summary>
    /// <param name="services">DI container being composed. Subclasses use
    /// <c>RemoveAll</c> + <c>AddSingleton</c> to swap implementations.</param>
    protected virtual void ConfigureAdditionalServices(IServiceCollection services)
    {
    }

    /// <summary>Stops the Kestrel host and disposes the application.</summary>
    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync().ConfigureAwait(false);
            await _app.DisposeAsync().ConfigureAwait(false);
        }
    }
}
