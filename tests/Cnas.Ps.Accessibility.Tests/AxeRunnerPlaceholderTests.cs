namespace Cnas.Ps.Accessibility.Tests;

/// <summary>
/// Tests the placeholder-detection contract of <see cref="AxeRunner"/>. The runner is
/// expected to expose a public sentinel — <see cref="AxeRunner.IsPlaceholderBundle"/>
/// — so the Playwright theory can decide whether to skip cleanly (offline dev) or
/// fail loud (CI without the real bundle).
/// </summary>
/// <remarks>
/// We deliberately do not invoke <c>RunAsync</c> from this test because that requires
/// Playwright + a launched browser. Detection of the placeholder is a pure file-read
/// operation and lives in its own helper method so we can test it without booting a
/// browser context.
/// </remarks>
public sealed class AxeRunnerPlaceholderTests
{
    /// <summary>
    /// The vendored bundle shipped with the repo carries the literal
    /// <c>// PLACEHOLDER</c> token on its very first line. The runner's sentinel must
    /// return <c>true</c> for that content so the Playwright theory tests skip rather
    /// than fail when developers run the suite offline.
    /// </summary>
    [Fact]
    public void IsPlaceholderBundle_ReturnsTrue_WhenContentStartsWithPlaceholderToken()
    {
        const string placeholder = "// PLACEHOLDER — replace with the real axe-core bundle.\nwindow.x=1;";

        AxeRunner.IsPlaceholderBundle(placeholder).Should().BeTrue();
    }

    /// <summary>
    /// A real axe-core bundle starts with <c>/*! axe v4.10.0 ... */</c>; the sentinel
    /// must return <c>false</c> so the Playwright theory tests run normally.
    /// </summary>
    [Fact]
    public void IsPlaceholderBundle_ReturnsFalse_WhenContentLooksLikeAxeCore()
    {
        const string real = "/*! axe v4.10.0\n * Copyright (c) 2024 Deque Systems, Inc.\n */ (function(){})();";

        AxeRunner.IsPlaceholderBundle(real).Should().BeFalse();
    }
}
