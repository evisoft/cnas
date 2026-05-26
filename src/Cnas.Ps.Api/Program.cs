using Cnas.Ps.Api.Composition;
using Cnas.Ps.Infrastructure;
using Cnas.Ps.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

// CLAUDE.md §1.8 — secrets never in source control. The committed appsettings.json
// ships an empty ConnectionStrings:Postgres on purpose; production deployments
// inject the real value via environment variables / Vault / k8s Secret. We
// fail loud at startup so a forgotten secret never silently becomes a runtime
// 500 on the first DB-bound request. The "Test" / "Testing" environments are
// exempted because test fixtures inject their own connection string via
// in-memory configuration BEFORE this guard runs — see ApiHostFixture.
var connStr = builder.Configuration.GetConnectionString("Postgres");
if (string.IsNullOrWhiteSpace(connStr)
    && !builder.Environment.IsEnvironment("Test")
    && !builder.Environment.IsEnvironment("Testing"))
{
    throw new InvalidOperationException(
        "ConnectionStrings:Postgres must be configured via environment variable, user secrets, or secret store. " +
        "It is intentionally empty in committed appsettings.json.");
}

builder.AddCnasObservability();
builder.Services.AddCnasApi(builder.Configuration);
// MCabinet is kept in a separate registration helper because the upstream
// citizen-portal contract is opt-in per environment (see XML doc on AddCnasMCabinet).
// AddCnasApi does NOT call it on its own, so Program.cs is the canonical wire-up site —
// otherwise IApplicationService activation fails at runtime with a DI resolution error
// because ApplicationServiceImpl takes IMCabinetPublisher in its ctor.
builder.Services.AddCnasMCabinet(builder.Configuration);
// R0117 / CF 14.11 — PGD (open-data portal) publisher. Kept separate from
// AddCnasInfrastructure for the same opt-in reason as MCabinet: blank base URL → no
// network call, dev/CI are safe by default.
builder.Services.AddCnasPgdPublisher(builder.Configuration);

var app = builder.Build();

app.UseCnasApiPipeline();

// Apply EF Core migrations at startup (idempotent, gated on connection availability).
if (!builder.Configuration.GetValue("Cnas:SkipMigrations", false))
{
    await DatabaseInitializer.ApplyMigrationsAsync(app.Services).ConfigureAwait(false);
}

await app.RunAsync().ConfigureAwait(false);

namespace Cnas.Ps.Api
{
    /// <summary>
    /// Exposed as <c>public partial</c> so <c>WebApplicationFactory&lt;Program&gt;</c> in
    /// <c>Cnas.Ps.Api.Tests</c> can target it. Has no body — the top-level statements above
    /// are this class's <c>Main</c>.
    /// </summary>
    public sealed partial class Program;
}
