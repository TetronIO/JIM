// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Guards the one way to link a Metaverse Object: <c>Utilities.GetMetaverseObjectHref</c>, which builds
/// <c>/t/{type plural name}/v/{id}</c>. Every hand-built link this replaced led to a page that does not exist:
/// <c>/users/{id}</c> (#2146), <c>/admin/metaverse/objects/{id}</c> and <c>/admin/metaverse/{id}</c>, the singular
/// type name in <c>/t/{name}/v/{id}</c> (#2151), and <c>/identity/search/{id}</c> before them.
/// <para>
/// A source sweep rather than a render test, for the same reason as <see cref="ObjectChipConventionTests"/>: each
/// link renders without complaint, and the defect is the next page building its own. It flags a string literal
/// that starts one of those routes: <c>"/t/.../v/</c>, <c>"/admin/metaverse/</c>, <c>"/identity/</c> or
/// <c>"/users/</c>. Comments and <c>@page</c> directives are skipped.
/// </para>
/// </summary>
[TestFixture]
public class MetaverseObjectHrefConventionTests
{
    /// <summary>
    /// A literal that genuinely is not a Metaverse Object link opts out with this marker on the line, or the two
    /// lines, immediately above it, saying why; the justification travels with the code.
    /// </summary>
    private const string ExemptionMarker = "metaverse-object-href: exempt";

    private const int ExemptionMarkerLookBehindLines = 2;

    private static readonly Regex HandBuiltHref = new(
        "\"/(?:t/[^\"\\s]*/v/|admin/metaverse(?:/|\")|identity/|users/)",
        RegexOptions.Compiled);

    private static readonly Lazy<string> RepositoryRoot = new(FindRepositoryRoot);

    [Test]
    public void MetaverseObjectLinks_AcrossJimWeb_UseGetMetaverseObjectHref()
    {
        var webRoot = Path.Join(RepositoryRoot.Value, "src", "JIM.Web");
        Assert.That(Directory.Exists(webRoot), Is.True, $"Expected to find JIM.Web sources at '{webRoot}'.");

        var offenders = Directory
            .EnumerateFiles(webRoot, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".razor", StringComparison.Ordinal) || path.EndsWith(".cs", StringComparison.Ordinal))
            .Where(path => !IsBuildOutput(path))
            .SelectMany(FindHandBuiltHrefs)
            .OrderBy(o => o.RelativePath, StringComparer.Ordinal)
            .ThenBy(o => o.LineNumber)
            .ToList();

        if (offenders.Count == 0)
            return;

        var message = new StringBuilder()
            .AppendLine($"{offenders.Count} hand-built Metaverse Object link(s) found. Use Utilities.GetMetaverseObjectHref instead:")
            .AppendLine();

        foreach (var offender in offenders)
            message.AppendLine($"  {offender.RelativePath}:{offender.LineNumber}  {offender.Text}");

        message
            .AppendLine()
            .AppendLine("A Metaverse Object's page is /t/{type plural name}/v/{id}; every hand-built variant so far")
            .AppendLine("has led to a page that does not exist. If the type's plural name is not to hand, carry it to")
            .AppendLine("the call site rather than guessing it.")
            .AppendLine()
            .AppendLine($"If the literal genuinely is not a Metaverse Object link, add a comment containing")
            .AppendLine($"\"{ExemptionMarker}\" directly above it, saying why.");

        Assert.Fail(message.ToString());
    }

    private static IEnumerable<Offender> FindHandBuiltHrefs(string path)
    {
        var lines = File.ReadAllLines(path);
        var relativePath = Path.GetRelativePath(RepositoryRoot.Value, path).Replace('\\', '/');

        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (IsCommentOrRouteDirective(trimmed) || !HandBuiltHref.IsMatch(lines[i]) || HasExemptionMarker(lines, i))
                continue;

            yield return new Offender(relativePath, i + 1, trimmed);
        }
    }

    private static bool IsCommentOrRouteDirective(string trimmedLine) =>
        trimmedLine.StartsWith("//", StringComparison.Ordinal) ||
        trimmedLine.StartsWith("*", StringComparison.Ordinal) ||
        trimmedLine.StartsWith("@*", StringComparison.Ordinal) ||
        trimmedLine.StartsWith("@page", StringComparison.Ordinal);

    private static bool IsBuildOutput(string path)
    {
        var separator = Path.DirectorySeparatorChar;
        return path.Contains($"{separator}obj{separator}", StringComparison.Ordinal) ||
               path.Contains($"{separator}bin{separator}", StringComparison.Ordinal);
    }

    private static bool HasExemptionMarker(IReadOnlyList<string> lines, int lineIndex)
    {
        var firstLine = Math.Max(0, lineIndex - ExemptionMarkerLookBehindLines);

        for (var i = firstLine; i < lineIndex; i++)
        {
            if (lines[i].Contains(ExemptionMarker, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Walks up from the test assembly's location to the directory holding <c>JIM.sln</c>. The test reads
    /// source files, so it needs the repository rather than the output directory.
    /// </summary>
    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory != null && !File.Exists(Path.Join(directory.FullName, "JIM.sln")))
            directory = directory.Parent;

        Assert.That(directory, Is.Not.Null, "Could not locate JIM.sln by walking up from the test output directory.");
        return directory!.FullName;
    }

    private sealed record Offender(string RelativePath, int LineNumber, string Text);
}
