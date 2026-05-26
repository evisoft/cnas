using System.IO;
using System.Text.RegularExpressions;

namespace Cnas.Ps.Architecture.Tests;

/// <summary>
/// Enforces the CLAUDE.md cross-cutting "UTC Everywhere" rule: no source file under
/// <c>src/</c> may call <see cref="DateTime.Now"/>, <see cref="DateTime.Today"/>, or
/// <see cref="DateTimeOffset.Now"/>. <see cref="DateTime.UtcNow"/> is permitted only
/// in <c>ITimeProvider.cs</c> (the system clock implementation); all other code MUST
/// route through <c>ICnasTimeProvider</c>.
/// </summary>
public class TimeProviderUsageTests
{
    /// <summary>
    /// Banned literal time-source expressions. Each entry is a regex that matches an
    /// actual call site (word-bounded so we don't match strings/comments containing the substring).
    /// </summary>
    private static readonly (string Pattern, string Description)[] BannedPatterns =
    [
        (@"\bDateTime\.Now\b", "DateTime.Now (use ICnasTimeProvider — system local time leaks timezone)"),
        (@"\bDateTime\.Today\b", "DateTime.Today (use ICnasTimeProvider.TodayUtc)"),
        (@"\bDateTimeOffset\.Now\b", "DateTimeOffset.Now (use ICnasTimeProvider)"),
    ];

    /// <summary>
    /// Pattern matching <c>DateTime.UtcNow</c>. Allowed only inside the files listed in
    /// <see cref="UtcNowAllowlist"/> — anywhere else, code must depend on ICnasTimeProvider.
    /// </summary>
    private static readonly Regex UtcNowPattern = new(@"\bDateTime\.UtcNow\b", RegexOptions.Compiled);

    /// <summary>
    /// Files allowed to call <see cref="DateTime.UtcNow"/> directly. Paths are repo-relative
    /// using forward slashes for portability across Windows / WSL / Linux CI runners.
    /// Only the system-clock implementation should ever appear here.
    /// </summary>
    private static readonly string[] UtcNowAllowlist =
    [
        "src/Cnas.Ps.Core/Common/ITimeProvider.cs",
    ];

    [Fact]
    public void Source_Code_Does_Not_Call_DateTime_Now_or_Today_Directly()
    {
        var repoRoot = LocateRepoRoot();
        var srcRoot = Path.Combine(repoRoot, "src");
        Directory.Exists(srcRoot).Should().BeTrue($"expected to find src/ directory under {repoRoot}");

        var violations = new List<string>();

        foreach (var file in Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories))
        {
            // Skip build artefacts that live alongside the source.
            var relative = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
            if (relative.Contains("/obj/", StringComparison.OrdinalIgnoreCase) ||
                relative.Contains("/bin/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var rawText = File.ReadAllText(file);
            // Strip comments and strings so a regex hit corresponds to a real call site, not a
            // mention inside an XML doc <example>, a `//` note, or a verbatim string literal.
            var text = StripCommentsAndStrings(rawText);

            // Hard-banned patterns — fail no matter where they appear.
            foreach (var (pattern, description) in BannedPatterns)
            {
                foreach (Match match in Regex.Matches(text, pattern))
                {
                    var line = LineOf(text, match.Index);
                    violations.Add($"{relative}:{line} — {description}");
                }
            }

            // DateTime.UtcNow is allowed only inside the allow-list files.
            if (UtcNowAllowlist.Contains(relative, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }
            foreach (Match match in UtcNowPattern.Matches(text))
            {
                var line = LineOf(text, match.Index);
                violations.Add(
                    $"{relative}:{line} — DateTime.UtcNow (inject ICnasTimeProvider and use clock.UtcNow). " +
                    "If this file is the system clock implementation, add it to UtcNowAllowlist.");
            }
        }

        violations.Should().BeEmpty(
            "UTC-everywhere violations were found. Either route through ICnasTimeProvider or, " +
            "for sanctioned files (e.g. SystemTimeProvider), add the path to UtcNowAllowlist.");
    }

    /// <summary>
    /// Walks up from the test assembly's directory until it finds the repository root,
    /// identified by the presence of a <c>src/</c> sibling next to <c>tests/</c>.
    /// </summary>
    private static string LocateRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src")) &&
                Directory.Exists(Path.Combine(dir.FullName, "tests")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "Could not locate repository root (looked for sibling src/ and tests/ directories starting from " +
            $"{AppContext.BaseDirectory}).");
    }

    /// <summary>Computes a 1-based line number for the supplied character offset.</summary>
    private static int LineOf(string text, int index)
    {
        var line = 1;
        for (var i = 0; i < index && i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                line++;
            }
        }
        return line;
    }

    /// <summary>
    /// Returns a copy of <paramref name="source"/> with single-line comments (<c>// ...</c>),
    /// XML doc comments (<c>/// ...</c>), block comments (<c>/* ... */</c>), and string/char
    /// literals (regular, verbatim, raw, and char) replaced by whitespace of equal length so
    /// that line offsets are preserved. This lets the regex scanner ignore <c>DateTime.Now</c>
    /// mentions that appear only in documentation or string content.
    /// </summary>
    private static string StripCommentsAndStrings(string source)
    {
        var buffer = source.ToCharArray();
        var i = 0;
        while (i < buffer.Length)
        {
            var c = buffer[i];

            // Line comment — includes XML doc comments (// and ///).
            if (c == '/' && i + 1 < buffer.Length && buffer[i + 1] == '/')
            {
                while (i < buffer.Length && buffer[i] != '\n')
                {
                    buffer[i++] = ' ';
                }
                continue;
            }

            // Block comment — preserve newlines so line numbers stay correct.
            if (c == '/' && i + 1 < buffer.Length && buffer[i + 1] == '*')
            {
                buffer[i++] = ' ';
                buffer[i++] = ' ';
                while (i < buffer.Length && !(buffer[i] == '*' && i + 1 < buffer.Length && buffer[i + 1] == '/'))
                {
                    if (buffer[i] != '\n') buffer[i] = ' ';
                    i++;
                }
                if (i < buffer.Length) { buffer[i++] = ' '; }
                if (i < buffer.Length) { buffer[i++] = ' '; }
                continue;
            }

            // Verbatim string @"..." — closes on a single ", "" is an escaped quote.
            if (c == '@' && i + 1 < buffer.Length && buffer[i + 1] == '"')
            {
                buffer[i++] = ' ';
                buffer[i++] = ' ';
                while (i < buffer.Length)
                {
                    if (buffer[i] == '"')
                    {
                        if (i + 1 < buffer.Length && buffer[i + 1] == '"')
                        {
                            buffer[i++] = ' '; buffer[i++] = ' ';
                            continue;
                        }
                        buffer[i++] = ' ';
                        break;
                    }
                    if (buffer[i] != '\n') buffer[i] = ' ';
                    i++;
                }
                continue;
            }

            // Regular string "..." — \" is an escaped quote.
            if (c == '"')
            {
                buffer[i++] = ' ';
                while (i < buffer.Length && buffer[i] != '"' && buffer[i] != '\n')
                {
                    if (buffer[i] == '\\' && i + 1 < buffer.Length)
                    {
                        buffer[i++] = ' '; buffer[i++] = ' ';
                        continue;
                    }
                    buffer[i++] = ' ';
                }
                if (i < buffer.Length && buffer[i] == '"') { buffer[i++] = ' '; }
                continue;
            }

            // Char literal '...'
            if (c == '\'')
            {
                buffer[i++] = ' ';
                while (i < buffer.Length && buffer[i] != '\'' && buffer[i] != '\n')
                {
                    if (buffer[i] == '\\' && i + 1 < buffer.Length)
                    {
                        buffer[i++] = ' '; buffer[i++] = ' ';
                        continue;
                    }
                    buffer[i++] = ' ';
                }
                if (i < buffer.Length && buffer[i] == '\'') { buffer[i++] = ' '; }
                continue;
            }

            i++;
        }
        return new string(buffer);
    }
}
