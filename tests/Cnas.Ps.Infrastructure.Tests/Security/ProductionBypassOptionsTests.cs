using Cnas.Ps.Infrastructure;
using Cnas.Ps.Infrastructure.MGov;
using Cnas.Ps.Infrastructure.Security;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.Tests.Security;

public sealed class ProductionBypassOptionsTests
{
    [Fact]
    public void AddCnasInfrastructure_ProductionTurnstileBypass_RejectsOptions()
    {
        var cfg = BuildConfig(new Dictionary<string, string?>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Production",
            ["Cnas:Captcha:Turnstile:BypassForTesting"] = "true",
        });
        var services = new ServiceCollection();

        services.AddCnasInfrastructure(cfg);
        using var sp = services.BuildServiceProvider();

        var act = () => sp.GetRequiredService<IOptions<TurnstileOptions>>().Value;
        act.Should().Throw<OptionsValidationException>()
            .WithMessage("*BypassForTesting*Production*");
    }

    [Fact]
    public void AddCnasMPassSaml_ProductionUnsignedAssertions_RejectsOptions()
    {
        var cfg = BuildConfig(new Dictionary<string, string?>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Production",
            ["Cnas:MGov:MPassSaml:IssuerUrl"] = "https://mpass.gov.md",
            ["Cnas:MGov:MPassSaml:ServiceProviderEntityId"] = "https://cnas.gov.md/sp",
            ["Cnas:MGov:MPassSaml:AllowUnsignedAssertionsForTesting"] = "true",
        });
        var services = new ServiceCollection();

        services.AddCnasMPassSaml(cfg);
        using var sp = services.BuildServiceProvider();

        var act = () => sp.GetRequiredService<IOptions<MPassSamlOptions>>().Value;
        act.Should().Throw<OptionsValidationException>()
            .WithMessage("*AllowUnsignedAssertionsForTesting*Production*");
    }

    private static IConfiguration BuildConfig(Dictionary<string, string?> overrides)
    {
        var values = new Dictionary<string, string?>(overrides)
        {
            ["ConnectionStrings:Postgres"] = "Host=localhost;Database=cnas;Username=cnas;Password=cnas",
            ["Minio:Endpoint"] = "localhost:9000",
        };
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
