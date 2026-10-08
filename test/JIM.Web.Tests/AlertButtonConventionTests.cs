// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.RegularExpressions;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Holds the design system's rule for buttons inside alerts: every one is an <c>&lt;AlertButton&gt;</c>, which renders
/// filled in the colour of the alert's severity (or, marked Secondary, as a text button inheriting it). Before the rule,
/// alert buttons were filled Primary, filled in their alert's colour, filled in an unrelated colour, outlined or plain
/// text, depending on who wrote the page. The component makes the colour a function of the severity, so the only thing a
/// call site can get wrong is handing it a different severity from its alert's, which this sweep also catches.
/// </summary>
/// <remarks>
/// An alert that is really a decision panel, whose buttons carry consequences of their own (a destructive apply in red
/// beside a safe one), opts out with an <c>@* alert-button: exempt - &lt;why&gt; *@</c> comment directly above its
/// <c>MudAlert</c>, so the reason travels with the markup.
/// </remarks>
[TestFixture]
public class AlertButtonConventionTests
{
    private const string ExemptionMarker = "alert-button: exempt";

    // Opening tags, tolerant of '>' inside quoted attribute values such as lambdas.
    private static readonly Regex Token = new(
        @"@\*.*?\*@" +
        @"|<MudAlert\b(?:[^>""']|""[^""]*""|'[^']*')*>" +
        @"|</MudAlert>" +
        @"|<(?:MudButton|MudMenu)\b" +
        @"|<AlertButton\b(?:[^>""']|""[^""]*""|'[^']*')*>",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex SeverityAttribute = new(@"\bSeverity=""([^""]*)""", RegexOptions.Compiled);

    [Test]
    public void EveryButtonInAnAlert_IsAnAlertButtonOfThatAlertsSeverity()
    {
        var webRoot = Path.Join(FindRepositoryRoot(), "src", "JIM.Web");
        Assert.That(Directory.Exists(webRoot), Is.True, $"Expected to find JIM.Web sources at '{webRoot}'.");

        var offenders = Directory
            .EnumerateFiles(webRoot, "*.razor", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                           !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .SelectMany(path => FindOffences(File.ReadAllText(path), Path.GetRelativePath(webRoot, path).Replace('\\', '/')))
            .ToList();

        Assert.That(offenders, Is.Empty,
            "Buttons inside alerts must be <AlertButton Severity=\"(the alert's Severity)\">, filled in the alert's colour, " +
            "or Secondary for a lesser action beside it (see src/JIM.Web/CLAUDE.md > Alerts):" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Test]
    public void FindOffences_ARawButtonInAnAlert_IsReported()
    {
        const string source = "<MudAlert Severity=\"Severity.Info\">\n  <MudButton Variant=\"Variant.Filled\">Go</MudButton>\n</MudAlert>";

        Assert.That(FindOffences(source, "Page.razor"), Has.Count.EqualTo(1));
    }

    [Test]
    public void FindOffences_AnAlertButtonOfADifferentSeverity_IsReported()
    {
        const string source = "<MudAlert Severity=\"Severity.Warning\">\n  <AlertButton Severity=\"Severity.Info\">Go</AlertButton>\n</MudAlert>";

        Assert.That(FindOffences(source, "Page.razor"), Has.Count.EqualTo(1));
    }

    [Test]
    public void FindOffences_AnAlertButtonNamingItsOwnVariantOrColour_IsReported()
    {
        const string source = "<MudAlert Severity=\"Severity.Info\">\n" +
                              "  <AlertButton Severity=\"Severity.Info\" Color=\"Color.Primary\">Go</AlertButton>\n" +
                              "  <AlertButton Severity=\"Severity.Info\" Variant=\"Variant.Outlined\">Go</AlertButton>\n" +
                              "</MudAlert>";

        Assert.That(FindOffences(source, "Page.razor"), Has.Count.EqualTo(2));
    }

    [Test]
    public void FindOffences_AnAlertButtonOutsideAnAlert_IsReported()
    {
        const string source = "<MudPaper>\n  <AlertButton Severity=\"Severity.Info\">Go</AlertButton>\n</MudPaper>";

        Assert.That(FindOffences(source, "Page.razor"), Has.Count.EqualTo(1));
    }

    [Test]
    public void FindOffences_ButtonsBelongToTheAlertNearestThem()
    {
        // The inner alert's button matches the inner alert; the outer one's matches the outer. Neither is an offence.
        const string source = "<MudAlert Severity=\"@PanelSeverity\">\n" +
                              "  <MudAlert Severity=\"Severity.Warning\">\n" +
                              "    <AlertButton Severity=\"Severity.Warning\">Inner</AlertButton>\n" +
                              "  </MudAlert>\n" +
                              "  <AlertButton Severity=\"@PanelSeverity\">Outer</AlertButton>\n" +
                              "</MudAlert>";

        Assert.That(FindOffences(source, "Page.razor"), Is.Empty);
    }

    [Test]
    public void FindOffences_AnExemptedAlert_IsNotChecked()
    {
        const string source = "@* alert-button: exempt - a decision panel *@\n" +
                              "<MudAlert Severity=\"Severity.Warning\">\n  <MudButton Color=\"Color.Error\">Remove</MudButton>\n</MudAlert>";

        Assert.That(FindOffences(source, "Page.razor"), Is.Empty);
    }

    [Test]
    public void FindOffences_ButtonsInsideAComment_AreIgnored()
    {
        const string source = "<MudAlert Severity=\"Severity.Info\">\n  @* <MudButton>Old</MudButton> *@\n</MudAlert>";

        Assert.That(FindOffences(source, "Page.razor"), Is.Empty);
    }

    /// <summary>
    /// The offences in one Razor source, as "file:line reason". Alerts nest, so each button is judged against the alert
    /// nearest to it; an alert under an exemption comment, and everything inside it, is skipped.
    /// </summary>
    private static List<string> FindOffences(string source, string relativePath)
    {
        var offences = new List<string>();
        var alerts = new Stack<(string Severity, bool Exempt)>();
        string? precedingComment = null;
        var precedingCommentEnd = -1;

        foreach (Match token in Token.Matches(source))
        {
            var text = token.Value;
            var line = source[..token.Index].Count(c => c == '\n') + 1;

            if (text.StartsWith("@*", StringComparison.Ordinal))
            {
                precedingComment = text;
                precedingCommentEnd = token.Index + token.Length;
                continue;
            }

            if (text.StartsWith("<MudAlert", StringComparison.Ordinal))
            {
                var directlyBelowComment = precedingComment != null &&
                                           string.IsNullOrWhiteSpace(source[precedingCommentEnd..token.Index]);
                var exempt = (directlyBelowComment && precedingComment!.Contains(ExemptionMarker, StringComparison.Ordinal)) ||
                             alerts.Any(alert => alert.Exempt);
                if (!text.EndsWith("/>", StringComparison.Ordinal))
                    alerts.Push((SeverityOf(text), exempt));
                continue;
            }

            if (text == "</MudAlert>")
            {
                if (alerts.Count > 0)
                    alerts.Pop();
                continue;
            }

            if (alerts.Count > 0 && alerts.Peek().Exempt)
                continue;

            if (!text.StartsWith("<AlertButton", StringComparison.Ordinal))
            {
                if (alerts.Count > 0)
                    offences.Add($"{relativePath}:{line} a {text[1..]} in an alert; use <AlertButton>");
                continue;
            }

            if (alerts.Count == 0)
            {
                offences.Add($"{relativePath}:{line} an AlertButton outside any alert");
                continue;
            }

            if (SeverityOf(text) != alerts.Peek().Severity)
                offences.Add($"{relativePath}:{line} an AlertButton whose Severity is not its alert's ({alerts.Peek().Severity})");

            if (Regex.IsMatch(text, @"\s(?:Variant|Color)="))
                offences.Add($"{relativePath}:{line} an AlertButton naming its own Variant or Color; the severity decides both");
        }

        return offences;
    }

    private static string SeverityOf(string tag)
    {
        var match = SeverityAttribute.Match(tag);
        return match.Success ? Regex.Replace(match.Groups[1].Value, @"\s+", string.Empty) : "(none)";
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory != null && !File.Exists(Path.Join(directory.FullName, "JIM.sln")))
            directory = directory.Parent;

        Assert.That(directory, Is.Not.Null, "Could not locate JIM.sln by walking up from the test output directory.");
        return directory!.FullName;
    }
}
