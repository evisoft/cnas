namespace Cnas.Ps.Api.Tests.Composition;

public sealed class OpenApiSecurityCompositionTests
{
    [Fact]
    public void Pipeline_OpenApiEndpointIsAuthorizedAndRateLimited()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "Cnas.Ps.Api",
            "Composition",
            "ApiCompositionRoot.cs"));

        source.Should().NotContain("app.MapOpenApi().DisableRateLimiting()");
        source.Should().Contain("app.MapOpenApi().RequireAuthorization(AuthorizationComposition.CnasTechAdmin)");
    }
}
