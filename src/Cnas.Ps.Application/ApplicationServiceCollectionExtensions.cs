using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Application.Localization;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace Cnas.Ps.Application;

/// <summary>
/// Application-layer service registration. Wires all UC services, validators, and
/// pipeline behaviours.
/// </summary>
public static class ApplicationServiceCollectionExtensions
{
    /// <summary>Adds Application-layer dependencies to <paramref name="services"/>.</summary>
    public static IServiceCollection AddCnasApplication(this IServiceCollection services)
    {
        services.AddValidatorsFromAssemblyContaining<ApplicationAssemblyMarker>();
        services.AddSingleton<IDecisionEngine, JsonRulesDecisionEngine>();
        // R0027 / TOR ARH 022 — culture-aware display-name resolver consulted by
        // callers rendering user-facing entities with the optional Name{Ro,Ru,En}
        // trio. Pure / stateless / thread-safe — registered as a singleton.
        services.AddSingleton<ILocalizedNameResolver, LocalizedNameResolver>();
        return services;
    }
}

/// <summary>Marker type used by reflection-based registration (e.g. FluentValidation).</summary>
public sealed class ApplicationAssemblyMarker;
