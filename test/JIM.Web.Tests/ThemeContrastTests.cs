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

    private static string BuildFailureMessage(List<string> failures)
    {
        var message = new StringBuilder();
        message.AppendLine(CultureInfo.InvariantCulture, $"{failures.Count} theme pairing(s) fall below WCAG AA ({AaFloor}:1):");
        message.AppendLine();
        foreach (var failure in failures)
            message.AppendLine(failure);
        message.AppendLine();
        message.AppendLine("Fix by giving the label the opposite lightness (a bright fill takes #000000dd, not white),");
        message.AppendLine("or, where the fill sits at the luminance that fails both label colours, by moving the fill");
        message.AppendLine("one step darker or lighter. Keep the matching --mud-palette-*-rgb variable in step.");
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
