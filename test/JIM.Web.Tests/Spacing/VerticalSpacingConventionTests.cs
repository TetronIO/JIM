// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text;
using System.Text.Json;
using NUnit.Framework;

namespace JIM.Web.Tests.Spacing;

/// <summary>
/// Guards the design system's vertical spacing (#3095): a gap is chosen by role (engineering/DESIGN.md > Layout >
/// Spacing) and applied by <c>SectionStack</c> and <c>SectionCaption</c>, never by a margin utility placed on a page.
/// <para>
/// The portal predates the rule, so the test ratchets, as <c>HardCodedTextConventionTests</c> does. Every
/// <c>.razor</c> file under <c>src/JIM.Web</c> has a recorded count of hand-placed vertical margins in
/// <c>vertical-spacing-baseline.json</c>: a file above its count fails, naming the lines; a new file must have none; a
/// file below its count also fails, asking for the baseline to be lowered, so migration is locked in and the total only
/// falls. Regenerate after migrating (never to make room) with
/// <c>JIM_UPDATE_SPACING_BASELINE=1 dotnet test test/JIM.Web.Tests --filter VerticalSpacingConventionTests</c>.
/// </para>
/// <para>
/// A deliberate exception (a specimen page showing every margin) carries a <c>spacing: exempt - &lt;why&gt;</c>
/// comment on its line or one of the two above, so the reason travels with the markup.
/// </para>
/// </summary>
[TestFixture]
public class VerticalSpacingConventionTests
{
    private const string BaselineFileName = "vertical-spacing-baseline.json";
    private const string UpdateBaselineVariable = "JIM_UPDATE_SPACING_BASELINE";

    private static readonly Lazy<string> RepositoryRoot = new(FindRepositoryRoot);

    [Test]
    public void PortalMarkup_HandPlacedVerticalMargins_NeverExceedTheRecordedBaseline()
    {
        var current = ScanPortal();
        var baselinePath = Path.Join(RepositoryRoot.Value, "test", "JIM.Web.Tests", "Spacing", BaselineFileName);

        if (Environment.GetEnvironmentVariable(UpdateBaselineVariable) == "1")
        {
            var counts = new SortedDictionary<string, int>(current.ToDictionary(entry => entry.Key, entry => entry.Value.Count), StringComparer.Ordinal);
            File.WriteAllText(baselinePath, JsonSerializer.Serialize(counts, new JsonSerializerOptions { WriteIndented = true }) + "\n");
            Assert.Pass($"Baseline rewritten: {counts.Values.Sum()} hand-placed vertical margins in {counts.Count} files.");
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
                    grown.AppendLine($"      line {finding.Line}: {finding.Token}");
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
                .AppendLine("Hand-placed vertical margins found beyond the spacing baseline:")
                .AppendLine()
                .Append(grown)
                .AppendLine()
                .AppendLine("Do not choose a gap on the page: put the blocks in a <SectionStack> (section gap), follow a block with")
                .AppendLine("<SectionCaption> for its helper text (caption gap), and see engineering/DESIGN.md > Layout > Spacing for")
                .AppendLine("which gap a role takes. Never copy a neighbouring page's margin. A genuine exception takes a comment")
                .AppendLine($"containing \"{VerticalSpacingScanner.ExemptionMarker} - <why>\" on its line or one of the two above.");
        }

        if (shrunk.Length > 0)
        {
            message
                .AppendLine()
                .AppendLine("Hand-placed vertical margins have gone down; lock the progress in by lowering the baseline:")
                .AppendLine()
                .Append(shrunk)
                .AppendLine()
                .AppendLine($"  {UpdateBaselineVariable}=1 dotnet test test/JIM.Web.Tests --filter VerticalSpacingConventionTests");
        }

        if (message.Length > 0)
            Assert.Fail(message.ToString());
    }

    /// <summary>Every portal <c>.razor</c> file with at least one finding, keyed by repository-relative path.</summary>
    private static SortedDictionary<string, IReadOnlyList<VerticalSpacingScanner.Finding>> ScanPortal()
    {
        var webRoot = Path.Join(RepositoryRoot.Value, "src", "JIM.Web");
        var results = new SortedDictionary<string, IReadOnlyList<VerticalSpacingScanner.Finding>>(StringComparer.Ordinal);

        foreach (var path in Directory.EnumerateFiles(webRoot, "*.razor", SearchOption.AllDirectories)
                     .Where(path => !Path.GetRelativePath(webRoot, path).Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj")))
        {
            var findings = VerticalSpacingScanner.Scan(File.ReadAllText(path));
            if (findings.Count > 0)
                results[Path.GetRelativePath(RepositoryRoot.Value, path).Replace('\\', '/')] = findings;
        }

        return results;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null && !File.Exists(Path.Join(directory.FullName, "JIM.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Could not find the repository root (JIM.sln).");
    }
}
