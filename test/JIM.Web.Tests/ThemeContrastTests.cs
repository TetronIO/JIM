// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Sweeps every theme stylesheet for foreground/background pairings that fall below WCAG AA, so a palette
/// edit cannot quietly ship text nobody can read.
///
/// This exists because the whole class of defect is invisible to every other check we have. A theme is data,
/// not code: it compiles, renders, and looks deliberate, and the only symptom is a label a person cannot read
/// in one theme out of twelve. Seven of the twelve were shipping at least one failing pairing when this was
/// written, the worst at 2.34:1 against a 4.5:1 floor, and nothing anywhere said so.
///
/// The pairings checked are the ones MudBlazor actually paints together: a filled control's label on its own
/// fill, and body text on the surfaces beneath it.
/// </summary>
[TestFixture]
public class ThemeContrastTests
{
    /// <summary>
    /// WCAG AA for body-sized text. MudBlazor's buttons and chips set their labels at 0.875rem, so the
    /// large-text allowance of 3.0 never applies to them.
    /// </summary>
    private const double AaFloor = 4.5;

    private static readonly Lazy<string> RepositoryRoot = new(FindRepositoryRoot);

    /// <summary>
    /// Foreground/background variable pairs, named as the thing a person sees.
    /// </summary>
    private static readonly (string Description, string Foreground, string Background)[] Pairings =
    [
        ("primary button label", "--mud-palette-primary-text", "--mud-palette-primary"),
        ("secondary button label", "--mud-palette-secondary-text", "--mud-palette-secondary"),
        ("tertiary button label", "--mud-palette-tertiary-text", "--mud-palette-tertiary"),
        ("success label", "--mud-palette-success-text", "--mud-palette-success"),
        ("warning label", "--mud-palette-warning-text", "--mud-palette-warning"),
        ("error label", "--mud-palette-error-text", "--mud-palette-error"),
        ("info label", "--mud-palette-info-text", "--mud-palette-info"),
        ("body text on surface", "--mud-palette-text-primary", "--mud-palette-surface"),
        ("body text on background", "--mud-palette-text-primary", "--mud-palette-background"),
        ("secondary text on surface", "--mud-palette-text-secondary", "--mud-palette-surface")
    ];

    [Test]
    public void EveryTheme_EveryPaintedPairing_MeetsWcagAa()
    {
        var themeDirectory = Path.Join(RepositoryRoot.Value, "src", "JIM.Web", "wwwroot", "css", "themes");
        Assert.That(Directory.Exists(themeDirectory), Is.True, $"Expected theme stylesheets at '{themeDirectory}'.");

        var themeFiles = Directory.EnumerateFiles(themeDirectory, "*.css")
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
        Assert.That(themeFiles, Is.Not.Empty, "Expected at least one theme stylesheet to check.");

        var failures = themeFiles.SelectMany(CheckTheme).ToList();

        Assert.That(failures, Is.Empty, () => BuildFailureMessage(failures));
    }

    /// <summary>
    /// The six semantic chip colours JIM restyles in <c>site.css</c>. Default and Dark are left to MudBlazor,
    /// which paints them as near-black on light grey and measures far above the floor.
    /// </summary>
    private static readonly string[] ChipColours = ["primary", "secondary", "tertiary", "info", "success", "warning", "error"];

    /// <summary>
    /// A Text-variant chip's label sits on a tint of its own colour, and an Outlined chip's on the bare surface.
    /// MudBlazor paints both labels in the raw palette colour, which is a fill colour chosen to sit behind white
    /// text and was never meant to be text itself; measured that way, seven of the twelve themes shipped primary
    /// chip labels below AA, the default dark theme at 2.80:1. This reads the actual chip rules out of
    /// <c>site.css</c> (label colour, tint, and their <c>color-mix()</c> recipes) so the measurement tracks
    /// whatever the stylesheet says rather than a copy of it kept here.
    /// </summary>
    [Test]
    public void EveryTheme_EveryChipLabel_MeetsWcagAa()
    {
        var siteCss = File.ReadAllText(Path.Join(RepositoryRoot.Value, "src", "JIM.Web", "wwwroot", "css", "site.css"));
        var siteVariables = ReadVariables(siteCss);
        var themeDirectory = Path.Join(RepositoryRoot.Value, "src", "JIM.Web", "wwwroot", "css", "themes");
        var themeFiles = Directory.EnumerateFiles(themeDirectory, "*.css").OrderBy(path => path, StringComparer.Ordinal).ToList();
        Assert.That(themeFiles, Is.Not.Empty, "Expected at least one theme stylesheet to check.");

        var failures = new List<string>();
        foreach (var themeFile in themeFiles)
        {
            var themeName = Path.GetFileName(themeFile);
            var themeVariables = ReadVariables(File.ReadAllText(themeFile));
            var variables = siteVariables.Concat(themeVariables)
                .GroupBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.Ordinal);

            var surface = Resolve(variables, "--mud-palette-surface");
            if (surface is null)
                continue;

            var themeCss = File.ReadAllText(themeFile);
            foreach (var colour in ChipColours)
            {
                if (Resolve(variables, $"--mud-palette-{colour}") is null)
                    continue;

                // MudBlazor's own defaults, which a site.css declaration overrides, which a theme's own
                // html[lang]-prefixed rule (higher specificity, and every one of them !important) overrides again.
                var mudDefaultLabel = $"var(--mud-palette-{colour})";
                var textRule = ReadDeclarations(siteCss, $".mud-chip.mud-chip-text.mud-chip-color-{colour}");
                foreach (var (property, value) in ReadDeclarations(themeCss, $"html[lang] .mud-chip-text.mud-chip-color-{colour}"))
                    textRule[property] = value;
                var outlinedRule = ReadDeclarations(siteCss, $".mud-chip.mud-chip-outlined.mud-chip-color-{colour}");
                foreach (var (property, value) in ReadDeclarations(themeCss, $"html[lang] .mud-chip-outlined.mud-chip-color-{colour}"))
                    outlinedRule[property] = value;

                var textLabel = ResolveExpression(textRule.GetValueOrDefault("color", mudDefaultLabel), variables, surface.Value);
                var textTint = ResolveExpression(textRule.GetValueOrDefault("background-color", "transparent"), variables, surface.Value);
                var outlinedLabel = ResolveExpression(outlinedRule.GetValueOrDefault("color", mudDefaultLabel), variables, surface.Value);

                Measure($"{colour} text chip label on its tint", textLabel, textTint);
                Measure($"{colour} outlined chip label on surface", outlinedLabel, surface.Value);

                void Measure(string description, (double R, double G, double B, double A)? foreground, (double R, double G, double B, double A)? background)
                {
                    if (foreground is null || background is null)
                    {
                        failures.Add($"  {themeName,-26} {description,-40} could not be resolved; the test's CSS reader needs extending");
                        return;
                    }

                    var composedBackground = Composite(background.Value, surface.Value);
                    var ratio = ContrastRatio(Composite(foreground.Value, composedBackground), composedBackground);
                    if (ratio < AaFloor)
                        failures.Add(string.Format(CultureInfo.InvariantCulture, "  {0,-26} {1,-40} {2:0.00} (needs {3})", themeName, description, ratio, AaFloor));
                }
            }
        }

        Assert.That(failures, Is.Empty, () => BuildFailureMessage(failures,
            "Fix in site.css, not the theme: raise the --jim-chip-text-* blend's share of text colour, or, if one",
            "theme's colour is the outlier, move that theme's palette colour a step further from its surface."));
    }

    /// <summary>
    /// The declarations of the first rule block whose selector list is exactly <paramref name="selector"/>,
    /// with any <c>!important</c> stripped. Empty when site.css has no such rule.
    /// </summary>
    private static Dictionary<string, string> ReadDeclarations(string css, string selector)
    {
        var block = Regex.Match(css, $@"(?<=^|\}}|\*/)\s*{Regex.Escape(selector)}\s*\{{([^}}]*)\}}", RegexOptions.Multiline);
        var declarations = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!block.Success)
            return declarations;

        foreach (var declaration in block.Groups[1].Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = declaration.IndexOf(':');
            if (colon > 0)
                declarations[declaration[..colon].Trim()] = declaration[(colon + 1)..].Replace("!important", string.Empty).Trim();
        }

        return declarations;
    }

    /// <summary>
    /// Evaluates the subset of CSS colour syntax the chip rules use: a literal colour, <c>transparent</c>,
    /// <c>var(--name)</c> (looked up in the merged site and theme variables, recursively), and
    /// <c>color-mix(in srgb, X P%, Y)</c> with either side any of the above. <c>transparent</c> mixes as the
    /// surface at zero alpha, so a tint of a colour over transparent composites the way the browser paints it.
    /// </summary>
    private static (double R, double G, double B, double A)? ResolveExpression(
        string expression, Dictionary<string, string> variables, (double R, double G, double B, double A) surface)
    {
        expression = expression.Trim();
        if (expression.Equals("transparent", StringComparison.OrdinalIgnoreCase))
            return (surface.R, surface.G, surface.B, 0d);

        var reference = Regex.Match(expression, @"^var\((--[\w-]+)\)$");
        if (reference.Success)
            return variables.TryGetValue(reference.Groups[1].Value, out var raw) ? ResolveExpression(raw, variables, surface) : null;

        var mix = Regex.Match(expression, @"^color-mix\(\s*in\s+srgb\s*,\s*(.+?)\s+(\d+(?:\.\d+)?)%\s*,\s*(.+)\)$", RegexOptions.IgnoreCase);
        if (mix.Success)
        {
            var first = ResolveExpression(mix.Groups[1].Value, variables, surface);
            var second = ResolveExpression(mix.Groups[3].Value, variables, surface);
            if (first is null || second is null)
                return null;

            // CSS color-mix in srgb interpolates premultiplied colour, then alpha, at the stated share.
            var share = double.Parse(mix.Groups[2].Value, CultureInfo.InvariantCulture) / 100d;
            var alpha = first.Value.A * share + second.Value.A * (1 - share);
            if (alpha == 0)
                return (surface.R, surface.G, surface.B, 0d);

            return ((first.Value.R * first.Value.A * share + second.Value.R * second.Value.A * (1 - share)) / alpha,
                (first.Value.G * first.Value.A * share + second.Value.G * second.Value.A * (1 - share)) / alpha,
                (first.Value.B * first.Value.A * share + second.Value.B * second.Value.A * (1 - share)) / alpha,
                alpha);
        }

        return ParseColour(expression);
    }

    private static IEnumerable<string> CheckTheme(string path)
    {
        var variables = ReadVariables(File.ReadAllText(path));
        var themeName = Path.GetFileName(path);

        // A theme that does not declare a pairing at all inherits it, and there is nothing here to judge.
        return Pairings
            .Select(pairing => (pairing, foreground: Resolve(variables, pairing.Foreground), background: Resolve(variables, pairing.Background)))
            .Where(candidate => candidate.foreground.HasValue && candidate.background.HasValue)
            .Select(candidate => (candidate.pairing, ratio: ContrastRatio(
                Composite(candidate.foreground!.Value, candidate.background!.Value), candidate.background!.Value)))
            .Where(measured => measured.ratio < AaFloor)
            .Select(measured => string.Format(
                CultureInfo.InvariantCulture,
                "  {0,-26} {1,-26} {2:0.00} (needs {3})  {4} on {5}",
                themeName, measured.pairing.Description, measured.ratio, AaFloor,
                variables[measured.pairing.Foreground], variables[measured.pairing.Background]));
    }

    private static string BuildFailureMessage(List<string> failures, params string[] advice)
    {
        var message = new StringBuilder();
        message.AppendLine(CultureInfo.InvariantCulture, $"{failures.Count} theme pairing(s) fall below WCAG AA ({AaFloor}:1):");
        message.AppendLine();
        foreach (var failure in failures)
            message.AppendLine(failure);
        message.AppendLine();
        if (advice.Length == 0)
        {
            advice =
            [
                "Fix by giving the label the opposite lightness (a bright fill takes #000000dd, not white),",
                "or, where the fill sits at the luminance that fails both label colours, by moving the fill",
                "one step darker or lighter. Keep the matching --mud-palette-*-rgb variable in step."
            ];
        }

        foreach (var line in advice)
            message.AppendLine(line);
        return message.ToString();
    }

    private static Dictionary<string, string> ReadVariables(string css)
    {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(css, @"^\s*(--[\w-]+):\s*([^;]+);", RegexOptions.Multiline))
            variables[match.Groups[1].Value] = match.Groups[2].Value.Trim();

        return variables;
    }

    private static (double R, double G, double B, double A)? Resolve(Dictionary<string, string> variables, string name)
    {
        return variables.TryGetValue(name, out var raw) ? ParseColour(raw) : null;
    }

    /// <summary>
    /// Parses <c>#rgb</c>, <c>#rrggbb</c>, <c>#rrggbbaa</c> and <c>rgb()</c>/<c>rgba()</c>. The alpha matters:
    /// the theme files express a dark label as <c>#000000dd</c>, and reading that as opaque black would
    /// overstate its contrast.
    /// </summary>
    private static (double R, double G, double B, double A)? ParseColour(string value)
    {
        value = value.Trim();

        var functional = Regex.Match(value, @"^rgba?\(([^)]+)\)$", RegexOptions.IgnoreCase);
        if (functional.Success)
        {
            var parts = functional.Groups[1].Value.Replace('/', ',').Split(',', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
                return null;

            var channels = parts.Take(4)
                .Select(part => double.TryParse(part.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                    ? (double?)parsed
                    : null)
                .ToList();
            if (channels.Take(3).Any(channel => channel is null))
                return null;

            return (channels[0]!.Value, channels[1]!.Value, channels[2]!.Value,
                channels.Count > 3 ? channels[3] ?? 1d : 1d);
        }

        if (!value.StartsWith('#'))
            return null;

        var hex = value[1..];
        if (hex.Length == 3)
            hex = string.Concat(hex.Select(c => new string(c, 2)));
        if (hex.Length is not (6 or 8) || !hex.All(Uri.IsHexDigit))
            return null;

        var r = Convert.ToInt32(hex[..2], 16);
        var g = Convert.ToInt32(hex.Substring(2, 2), 16);
        var b = Convert.ToInt32(hex.Substring(4, 2), 16);
        var a = hex.Length == 8 ? Convert.ToInt32(hex.Substring(6, 2), 16) / 255d : 1d;
        return (r, g, b, a);
    }

    /// <summary>
    /// Composites a foreground with alpha over its opaque background, which is what the eye actually receives.
    /// </summary>
    private static (double R, double G, double B, double A) Composite(
        (double R, double G, double B, double A) foreground, (double R, double G, double B, double A) background)
    {
        return (foreground.R * foreground.A + background.R * (1 - foreground.A),
            foreground.G * foreground.A + background.G * (1 - foreground.A),
            foreground.B * foreground.A + background.B * (1 - foreground.A),
            1d);
    }

    private static double ContrastRatio((double R, double G, double B, double A) first, (double R, double G, double B, double A) second)
    {
        var firstLuminance = RelativeLuminance(first);
        var secondLuminance = RelativeLuminance(second);
        var lighter = Math.Max(firstLuminance, secondLuminance);
        var darker = Math.Min(firstLuminance, secondLuminance);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double RelativeLuminance((double R, double G, double B, double A) colour)
    {
        return 0.2126 * Channel(colour.R) + 0.7152 * Channel(colour.G) + 0.0722 * Channel(colour.B);

        static double Channel(double value)
        {
            value /= 255d;
            return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
    }

    /// <summary>
    /// Walks up from the test assembly's location to the directory holding <c>JIM.sln</c>. The test reads the
    /// theme stylesheets, so it needs the repository rather than the output directory.
    /// </summary>
    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory != null && !File.Exists(Path.Join(directory.FullName, "JIM.sln")))
            directory = directory.Parent;

        Assert.That(directory, Is.Not.Null, "Could not locate JIM.sln by walking up from the test output directory.");
        return directory!.FullName;
    }
}
