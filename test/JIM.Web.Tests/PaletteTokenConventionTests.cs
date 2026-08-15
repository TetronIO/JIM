// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Guards JIM's own stylesheets against depending on a MudBlazor palette token that JIM's themes do not set.
/// <para>
/// MudBlazor emits its full default palette at <c>:root</c>, and each theme file in
/// <c>src/JIM.Web/wwwroot/css/themes/</c> overrides the subset it cares about. A rule that reaches for a token outside
/// that subset therefore resolves, renders, and silently paints MudBlazor's stock colour in every theme JIM ships. The
/// failure is invisible to <c>dotnet build</c>, to bUnit (which applies no stylesheet), and to a screenshot of the one
/// theme the author happened to be using.
/// </para>
/// <para>
/// Two shipped examples motivated this test. <c>--mud-palette-primary-hover</c> painted selected rows and cards in
/// MudBlazor's stock indigo at 6% alpha whatever the theme's accent actually was, which on the dark themes is close to
/// invisible; and <c>--mud-palette-background-gray</c> (the American spelling; the themes set <c>-grey</c>) resolves to
/// an opaque <c>rgb(245,245,245)</c>, so the audit chip's hover state painted near-white under near-white text on every
/// dark theme, measured at 1.16:1.
/// </para>
/// <para>
/// The fix in both cases is to derive the colour from a themed token rather than to add the missing one: JIM's themes
/// deliberately do not own MudBlazor's internal hover tokens, and defining them would change MudBlazor's own components
/// app-wide.
/// </para>
/// </summary>
[TestFixture]
public class PaletteTokenConventionTests
{
    /// <summary>
    /// A reference to a token JIM's themes deliberately do not set opts out with this marker in a comment on the line
    /// (or the two lines) above it, saying why the stock MudBlazor value is the intended one. The justification then
    /// travels with the rule, as it does for the search-box convention.
    /// </summary>
    private const string ExemptionMarker = "palette-token: exempt";

    /// <summary>How many lines above the reference are searched for <see cref="ExemptionMarker"/>.</summary>
    private const int ExemptionMarkerLookBehindLines = 2;

    private static readonly Lazy<string> RepositoryRoot = new(FindRepositoryRoot);

    [Test]
    public void JimStylesheets_OnlyUsePaletteTokensEveryThemeSets()
    {
        var webRoot = Path.Join(RepositoryRoot.Value, "src", "JIM.Web");
        Assert.That(Directory.Exists(webRoot), Is.True, $"Expected to find JIM.Web sources at '{webRoot}'.");

        var themes = ReadThemeCoverage(webRoot);
        Assert.That(themes.ThemeCount, Is.GreaterThan(0), "Expected at least one theme file to read token coverage from.");

        var offenders = EnumerateJimStylesheets(webRoot)
            .SelectMany(path => FindUnthemedTokenUses(path, themes))
            .OrderBy(o => o.RelativePath, StringComparer.Ordinal)
            .ThenBy(o => o.LineNumber)
            .ToList();

        if (offenders.Count == 0)
            return;

        var message = new StringBuilder()
            .AppendLine($"{offenders.Count} reference(s) to a palette token JIM's themes do not set:")
            .AppendLine();

        foreach (var offender in offenders)
            message.AppendLine($"  {offender.RelativePath}:{offender.LineNumber}  {offender.Token} (set by {offender.ThemesSetting} of {themes.ThemeCount} themes)");

        message
            .AppendLine()
            .AppendLine("Such a token resolves to MudBlazor's stock default and does not follow the theme, so the rule")
            .AppendLine("paints the same colour in all of them. Derive the colour from a themed token instead, e.g.")
            .AppendLine("  color-mix(in srgb, var(--mud-palette-primary) 12%, transparent)   for an accent tint")
            .AppendLine("  color-mix(in srgb, var(--mud-palette-text-primary) 8%, var(--mud-palette-surface))")
            .AppendLine("                                                                    to raise a surface")
            .AppendLine()
            .AppendLine("Do not fix this by adding the token to the theme files: MudBlazor's own components read these")
            .AppendLine("tokens too, so defining one changes rendering well beyond the rule you are editing.")
            .AppendLine()
            .AppendLine($"If the stock value is genuinely what is wanted, put \"{ExemptionMarker}\" in a comment")
            .AppendLine("directly above the declaration, saying why.");

        Assert.Fail(message.ToString());
    }

    /// <summary>
    /// Every theme must set the same tokens. A token one theme sets and another does not is the same defect seen from
    /// the other side: the rule reading it follows the theme on some and falls back to MudBlazor's stock on the rest.
    /// </summary>
    [Test]
    public void EveryTheme_SetsTheSamePaletteTokens()
    {
        var webRoot = Path.Join(RepositoryRoot.Value, "src", "JIM.Web");
        var themes = ReadThemeCoverage(webRoot);

        var partial = themes.SettersByToken
            .Where(pair => pair.Value.Count != themes.ThemeCount)
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToList();

        if (partial.Count == 0)
            return;

        var message = new StringBuilder()
            .AppendLine($"{partial.Count} palette token(s) are set by some themes but not all {themes.ThemeCount}:")
            .AppendLine();

        foreach (var (token, setters) in partial)
        {
            var missing = themes.ThemeNames.Except(setters, StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal);
            message.AppendLine($"  {token} — missing from: {string.Join(", ", missing)}");
        }

        Assert.Fail(message.ToString());
    }

    /// <summary>
    /// JIM's own stylesheets: the shared sheets and every component-scoped sheet. The theme files are excluded because
    /// they are what defines the tokens; MudBlazor's own sheet is not ours to police.
    /// </summary>
    private static IEnumerable<string> EnumerateJimStylesheets(string webRoot)
    {
        var themesDirectory = Path.Join(webRoot, "wwwroot", "css", "themes") + Path.DirectorySeparatorChar;

        return Directory
            .EnumerateFiles(webRoot, "*.css", SearchOption.AllDirectories)
            .Where(path => !path.StartsWith(themesDirectory, StringComparison.Ordinal))
            .Where(path => !IsUnderBuildOutput(path));
    }

    private static bool IsUnderBuildOutput(string path)
    {
        var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Any(part => string.Equals(part, "obj", StringComparison.Ordinal) || string.Equals(part, "bin", StringComparison.Ordinal));
    }

    private static ThemeCoverage ReadThemeCoverage(string webRoot)
    {
        var themesDirectory = Path.Join(webRoot, "wwwroot", "css", "themes");
        Assert.That(Directory.Exists(themesDirectory), Is.True, $"Expected theme files at '{themesDirectory}'.");

        var settersByToken = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var themeNames = new List<string>();

        foreach (var path in Directory.EnumerateFiles(themesDirectory, "*.css").OrderBy(p => p, StringComparer.Ordinal))
        {
            var themeName = Path.GetFileNameWithoutExtension(path);
            themeNames.Add(themeName);

            foreach (Match match in Regex.Matches(File.ReadAllText(path), @"^\s*(--mud-palette-[a-z0-9-]+)\s*:", RegexOptions.Multiline))
            {
                if (!settersByToken.TryGetValue(match.Groups[1].Value, out var setters))
                {
                    setters = new HashSet<string>(StringComparer.Ordinal);
                    settersByToken[match.Groups[1].Value] = setters;
                }

                setters.Add(themeName);
            }
        }

        return new ThemeCoverage(themeNames, settersByToken);
    }

    private static IEnumerable<Offender> FindUnthemedTokenUses(string path, ThemeCoverage themes)
    {
        var lines = File.ReadAllLines(path);
        var relativePath = Path.GetRelativePath(RepositoryRoot.Value, path).Replace('\\', '/');

        for (var i = 0; i < lines.Length; i++)
        {
            // A commented-out line, or prose in a comment naming a token, is not a declaration.
            if (lines[i].TrimStart().StartsWith("*", StringComparison.Ordinal) || lines[i].Contains("/*", StringComparison.Ordinal))
                continue;

            // Hoisted out of the projection below because it is a property of the line, not of any one token on
            // it: an exemption comment above a declaration exempts every token that declaration reads.
            if (HasExemptionMarker(lines, i))
                continue;

            var lineNumber = i + 1;

            // One pipeline rather than a foreach that maps its iteration variable and then guards the body: the
            // two halves are flagged separately (Select and Where), and converting only one leaves the other.
            foreach (var offender in Regex.Matches(lines[i], @"var\(\s*(--mud-palette-[a-z0-9-]+)")
                         .Select(match => match.Groups[1].Value)
                         .Select(token => (token, setters: themes.SettersByToken.TryGetValue(token, out var found) ? found.Count : 0))
                         .Where(candidate => candidate.setters != themes.ThemeCount)
                         .Select(candidate => new Offender(relativePath, lineNumber, candidate.token, candidate.setters)))
            {
                yield return offender;
            }
        }
    }

    private static bool HasExemptionMarker(IReadOnlyList<string> lines, int lineIndex)
    {
        var firstLine = Math.Max(0, lineIndex - ExemptionMarkerLookBehindLines);

        for (var i = firstLine; i <= lineIndex; i++)
        {
            if (lines[i].Contains(ExemptionMarker, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Walks up from the test assembly's location to the directory holding <c>JIM.sln</c>. The test reads source
    /// files, so it needs the repository rather than the output directory.
    /// </summary>
    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);

        while (directory != null && !File.Exists(Path.Join(directory.FullName, "JIM.sln")))
            directory = directory.Parent;

        Assert.That(directory, Is.Not.Null, "Could not locate the repository root (no JIM.sln found walking up from the test directory).");
        return directory!.FullName;
    }

    private sealed record ThemeCoverage(IReadOnlyList<string> ThemeNames, Dictionary<string, HashSet<string>> SettersByToken)
    {
        public int ThemeCount => ThemeNames.Count;
    }

    private sealed record Offender(string RelativePath, int LineNumber, string Token, int ThemesSetting);
}
