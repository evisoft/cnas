using System.Runtime.Serialization;

namespace Cnas.Ps.Accessibility.Tests;

/// <summary>
/// Thrown by <see cref="AxeRunner.RunAsync"/> when the embedded
/// <c>Resources/axe.min.js</c> file is the committed placeholder rather than a real
/// axe-core bundle. The Playwright theory translates this into a logged warning
/// (offline dev) — the dedicated CI accessibility job overwrites the file with the
/// real bundle before invoking <c>dotnet test</c>, so this exception in CI signals a
/// pipeline misconfiguration.
/// </summary>
public sealed class AxeBundleMissingException : InvalidOperationException
{
    /// <summary>Initialises the exception with the default explanatory message.</summary>
    public AxeBundleMissingException()
        : base("axe-core bundle missing — the vendored Resources/axe.min.js is the placeholder. "
            + "The CI accessibility job must download the real bundle from the axe-core GitHub release "
            + "before invoking dotnet test.")
    {
    }

    /// <summary>Initialises the exception with a caller-supplied message.</summary>
    /// <param name="message">The descriptive message.</param>
    public AxeBundleMissingException(string message)
        : base(message)
    {
    }

    /// <summary>Initialises the exception with a message and inner exception.</summary>
    /// <param name="message">The descriptive message.</param>
    /// <param name="innerException">The triggering exception.</param>
    public AxeBundleMissingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
