// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Guards the "values in sentences" convention documented in <c>src/JIM.Web/CLAUDE.md</c>: a value or name placed into
/// a sentence as inline code is built with <c>InlineCode.Markup</c> or <c>InlineCode.Format</c>, never by writing the
/// <c>jim-code-inline</c> element into a C# string by hand.
/// <para>
/// A hand-written element is markup assembled from strings, which Razor does not encode, so every copy has to remember
/// to HTML-encode its value. The shared helper encodes in one place. A Razor element in markup
/// (<c>&lt;code class="jim-code-inline"&gt;@value&lt;/code&gt;</c>) is outside the convention: Razor encodes it itself.
/// So the sweep looks only for the element inside a C# string, which in a <c>.razor</c> file shows as an escaped or
/// doubled quote after <c>class=</c>, and in a <c>.cs</c> file is any mention at all.
/// </para>
/// </summary>
[TestFixture]
public class InlineCodeConventionTests
{
    /// <summary>The helper that owns the convention: the one place the element is written as a string.</summary>
    private const string HelperFileName = "InlineCode.cs";

    private const string ClassName = "jim-code-inline";

    /// <summary>
    /// The class attribute inside a C# string literal in a Razor file: <c>class=\"</c> in a regular string, or
    /// <c>class=""</c> in a verbatim one.
    /// </summary>
    private static readonly Regex RazorStringElement = new(@"class\s*=\s*(?:\\""|"""")[^""\\]*\bjim-code-inline\b", RegexOptions.Compiled);

    private static readonly Lazy<string> RepositoryRoot = new(FindRepositoryRoot);

    [Test]
    public void InlineCodeMarkup_AcrossJimWeb_IsBuiltWithTheSharedHelper()
    {
        var webRoot = Path.Join(RepositoryRoot.Value, "src", "JIM.Web");
        Assert.That(Directory.Exists(webRoot), Is.True, $"Expected to find JIM.Web sources at '{webRoot}'.");

        var offenders = Directory
            .EnumerateFiles(webRoot, "*.*", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(webRoot, path))
            .Where(path => !string.Equals(Path.GetFileName(path), HelperFileName, StringComparison.Ordinal))
            .SelectMany(FindHandWrittenElements)
            .OrderBy(o => o.RelativePath, StringComparer.Ordinal)
            .ThenBy(o => o.LineNumber)
            .ToList();

        if (offenders.Count == 0)
            return;

        var message = new StringBuilder()
            .AppendLine($"{offenders.Count} hand-written {ClassName} element(s) found in C# strings. Use InlineCode instead:")
            .AppendLine();

        foreach (var offender in offenders)
            message.AppendLine($"  {offender.RelativePath}:{offender.LineNumber}");

        message
            .AppendLine()
            .AppendLine("InlineCode.Markup(value) gives one encoded value as inline code; InlineCode.Format(template, values)")
            .AppendLine("composes a whole localised message. See src/JIM.Web/CLAUDE.md > \"Values in sentences\".");

        Assert.Fail(message.ToString());
    }

    private static IEnumerable<Offender> FindHandWrittenElements(string path)
    {
        var extension = Path.GetExtension(path);
        Func<string, bool>? isOffending = extension switch
        {
            ".razor" => line => RazorStringElement.IsMatch(line),
            // A .cs file has no Razor markup, so any mention of the class is a string building the element.
            ".cs" => line => line.Contains(ClassName, StringComparison.Ordinal),
            _ => null
        };

        if (isOffending == null)
            yield break;

        var lines = File.ReadAllLines(path);
        var relativePath = Path.GetRelativePath(RepositoryRoot.Value, path).Replace('\\', '/');

        for (var i = 0; i < lines.Length; i++)
        {
            if (isOffending(lines[i]))
                yield return new Offender(relativePath, i + 1);
        }
    }

    private static bool IsBuildOutput(string webRoot, string path)
    {
        var relative = Path.GetRelativePath(webRoot, path).Replace('\\', '/');
        return relative.StartsWith("bin/", StringComparison.Ordinal) || relative.StartsWith("obj/", StringComparison.Ordinal);
    }

    /// <summary>
    /// Walks up from the test assembly's location to the directory holding <c>JIM.sln</c>. The test reads source
    /// files, so it needs the repository rather than the output directory.
    /// </summary>
    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory != null && !File.Exists(Path.Join(directory.FullName, "JIM.sln")))
            directory = directory.Parent;

        Assert.That(directory, Is.Not.Null, "Could not locate JIM.sln by walking up from the test output directory.");
        return directory!.FullName;
    }

    private sealed record Offender(string RelativePath, int LineNumber);
}
