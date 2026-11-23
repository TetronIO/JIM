// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text;
using System.Text.Json;
using NUnit.Framework;

namespace JIM.Web.Tests.Localisation;

/// <summary>
/// Guards the portal's localisation (#3030): user-visible text lives in <c>src/JIM.Web/Resources/*.resx</c>, never
/// written straight into a page or component, so every change to the portal's wording reaches every language.
/// <para>
/// The portal predates this rule, so the test ratchets rather than demanding zero at once. Every scanned file has a
/// recorded count of hard-coded text in <c>hard-coded-text-baseline.json</c>:
/// <list type="bullet">
///   <item>a file with more than its count fails, naming the lines, so new or edited text cannot go in hard-coded;</item>
///   <item>a new file must have none at all;</item>
///   <item>a file with fewer than its count also fails, asking for the baseline to be lowered, so conversion work is
///   locked in and the recorded total only ever falls.</item>
/// </list>
/// Regenerate the baseline after converting text (never to make room for new text) with
/// <c>JIM_UPDATE_LOCALISATION_BASELINE=1 dotnet test test/JIM.Web.Tests --filter HardCodedTextConventionTests</c>.
/// </para>
/// <para>
/// Text that is deliberately not translated (a product name in a sentence the scanner misreads, a developer-only
/// specimen page) carries an <c>i18n: exempt - &lt;why&gt;</c> comment on its line or one of the two above, so
/// the reason travels with the markup, as every other convention sweep in this project does.
/// </para>
/// </summary>
[TestFixture]
public class HardCodedTextConventionTests
{
    private const string BaselineFileName = "hard-coded-text-baseline.json";
    private const string UpdateBaselineVariable = "JIM_UPDATE_LOCALISATION_BASELINE";

    /// <summary>
    /// Folders whose text is not the portal's: the REST API and SCIM (machine-readable contracts, out of scope for
    /// translation), middleware, hosting and logging (diagnostics), and the resources themselves.
    /// </summary>
    private static readonly string[] ExcludedFolders =
    [
        "bin", "obj", "Resources", "Localisation", "Controllers", "Middleware", "Extensions", "Scim", "Hubs",
        "Logging", Path.Join("Models", "Api")
    ];

    /// <summary>
    /// Single files whose text is not the portal's: the host, and the REST API's bearer-token refusals and audit event
    /// names (Activity text, which is stored, is tracked separately from the portal's own wording).
    /// </summary>
    private static readonly string[] ExcludedFiles =
    [
        "Program.cs",
        Path.Join("Authentication", "AccessTokenAuthenticationAudit.cs"),
        Path.Join("Authentication", "BearerScopeRequirement.cs"),
        Path.Join("Authentication", "FailedAuthenticationEventNames.cs"),
        Path.Join("Authentication", "JimJwtBearerOptions.cs")
    ];

    private static readonly Lazy<string> RepositoryRoot = new(FindRepositoryRoot);

    [Test]
    public void PortalSource_HardCodedText_NeverExceedsTheRecordedBaseline()
    {
        var current = ScanPortal();
        var baselinePath = Path.Join(RepositoryRoot.Value, "test", "JIM.Web.Tests", "Localisation", BaselineFileName);

        if (Environment.GetEnvironmentVariable(UpdateBaselineVariable) == "1")
        {
            WriteBaseline(baselinePath, current);
            Assert.Pass($"Baseline rewritten: {current.Values.Sum(f => f.Count)} hard-coded strings in {current.Count} files.");
        }

        Assert.That(File.Exists(baselinePath), Is.True, $"Expected the baseline at '{baselinePath}'.");
        var baseline = JsonSerializer.Deserialize<SortedDictionary<string, int>>(File.ReadAllText(baselinePath))!;

        var grown = new StringBuilder();
        var shrunk = new StringBuilder();

        foreach (var (file, findings) in current)
        {
            var allowed = baseline.GetValueOrDefault(file, 0);
            if (findings.Count > allowed)
            {
                grown.AppendLine($"  {file}: {findings.Count} found, {allowed} allowed");
                foreach (var finding in findings)
                    grown.AppendLine($"      line {finding.Line}: \"{Truncate(finding.Text)}\"");
            }
            else if (findings.Count < allowed)
            {
                shrunk.AppendLine($"  {file}: {findings.Count} found, baseline says {allowed}");
            }
        }

        foreach (var (file, allowed) in baseline.Where(entry => !current.ContainsKey(entry.Key) && entry.Value > 0))
            shrunk.AppendLine($"  {file}: none found, baseline says {allowed}");

        var message = new StringBuilder();
        if (grown.Length > 0)
        {
            message
                .AppendLine("Hard-coded user-visible text found beyond the localisation baseline:")
                .AppendLine()
                .Append(grown)
                .AppendLine()
                .AppendLine("Put the text in a resource file and read it through its typed accessor, e.g. @LayoutText.SignOut,")
                .AppendLine("with a translation for every language (src/JIM.Web/CLAUDE.md > \"Localisation\"). If the text is")
                .AppendLine($"genuinely not for translation, add a comment containing \"{HardCodedTextScanner.ExemptionMarker} - <why>\" on its line")
                .AppendLine("or directly above it. The list above is every finding in the file; the new ones are among them.");
        }

        if (shrunk.Length > 0)
        {
            message
                .AppendLine()
                .AppendLine("Hard-coded text has gone down; lock the progress in by lowering the baseline:")
                .AppendLine()
                .Append(shrunk)
                .AppendLine()
                .AppendLine($"  {UpdateBaselineVariable}=1 dotnet test test/JIM.Web.Tests --filter HardCodedTextConventionTests");
        }

        if (message.Length > 0)
            Assert.Fail(message.ToString());
    }

    /// <summary>
    /// Writes every finding, file by file, to the path in <c>JIM_LOCALISATION_REPORT</c>: the work list for converting
    /// an area. Explicit, so it only runs when asked for by name.
    /// </summary>
    [Test]
    [Explicit("A report for conversion work, not a check.")]
    public void WriteHardCodedTextReport()
    {
        var reportPath = Environment.GetEnvironmentVariable("JIM_LOCALISATION_REPORT");
        Assume.That(reportPath, Is.Not.Null.And.Not.Empty, "Set JIM_LOCALISATION_REPORT to the file to write.");

        var report = new StringBuilder();
        foreach (var (file, findings) in ScanPortal())
        {
            report.AppendLine($"{file} ({findings.Count})");
            foreach (var finding in findings)
                report.AppendLine($"  {finding.Line}: {finding.Text}");
        }

        File.WriteAllText(reportPath!, report.ToString());
    }

    /// <summary>Every scanned portal file with at least one finding, keyed by repository-relative path.</summary>
    internal static SortedDictionary<string, IReadOnlyList<HardCodedTextScanner.Finding>> ScanPortal()
    {
        var webRoot = Path.Join(RepositoryRoot.Value, "src", "JIM.Web");
        var results = new SortedDictionary<string, IReadOnlyList<HardCodedTextScanner.Finding>>(StringComparer.Ordinal);

        foreach (var path in Directory.EnumerateFiles(webRoot, "*.*", SearchOption.AllDirectories))
        {
            var relativeToWeb = Path.GetRelativePath(webRoot, path);
            if (ExcludedFolders.Any(folder => relativeToWeb.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                || ExcludedFiles.Contains(relativeToWeb))
                continue;

            var extension = Path.GetExtension(path);
            IReadOnlyList<HardCodedTextScanner.Finding> findings = extension switch
            {
                ".razor" or ".cshtml" => HardCodedTextScanner.ScanRazor(File.ReadAllText(path)),
                ".cs" when !path.EndsWith(".Designer.cs", StringComparison.Ordinal) => HardCodedTextScanner.ScanCSharp(File.ReadAllText(path)),
                _ => []
            };

            if (findings.Count > 0)
                results[Path.GetRelativePath(RepositoryRoot.Value, path).Replace('\\', '/')] = findings;
        }

        return results;
    }

    private static void WriteBaseline(string path, SortedDictionary<string, IReadOnlyList<HardCodedTextScanner.Finding>> current)
    {
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var (file, findings) in current)
            counts[file] = findings.Count;

        File.WriteAllText(path, JsonSerializer.Serialize(counts, new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    private static string Truncate(string text) => text.Length <= 100 ? text : text[..97] + "...";

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null && !File.Exists(Path.Join(directory.FullName, "JIM.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Could not find the repository root (JIM.sln).");
    }
}
