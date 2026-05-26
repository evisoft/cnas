using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Cnas.Ps.Infrastructure.Persistence;

/// <summary>
/// Design-time factory for <see cref="CnasDbContext"/> used by the EF Core
/// CLI tooling (<c>dotnet ef migrations add</c>, <c>dotnet ef dbcontext info</c>, ...).
/// Resolves the constructor unambiguously by selecting the single-argument
/// overload — design-time tooling does not need a live
/// <see cref="Cnas.Ps.Application.Abstractions.IFieldEncryptor"/> because no
/// data is read or written during model scaffolding.
/// </summary>
/// <remarks>
/// <para>
/// The runtime composition root still uses the two-argument constructor via
/// <c>AddDbContext</c> + <c>ActivatorUtilities</c>; this factory only kicks
/// in when the dotnet ef tool boots a transient context purely to read the
/// model. Without it, the tool reports "Multiple constructors accepting all
/// given argument types have been found" because both ctors look valid to
/// the activator from a design-time perspective.
/// </para>
/// <para>
/// The connection string is a placeholder — design-time operations work
/// against the model only and never open a connection.
/// </para>
/// </remarks>
public sealed class CnasDbContextDesignTimeFactory : IDesignTimeDbContextFactory<CnasDbContext>
{
    /// <inheritdoc />
    public CnasDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<CnasDbContext>()
            .UseNpgsql("Host=localhost;Database=cnas_design_time;Username=design;Password=design")
            .Options;
        return new CnasDbContext(options);
    }
}
