// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.RegularExpressions;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Keeps random (version 4) ids out of the rows JIM's raw SQL writers persist (#2929).
/// </summary>
/// <remarks>
/// <para>
/// A row whose id is random lands on a random page of every index keyed on it: the table's primary key, and each child
/// table's index on its parent's id. In JIM's largest tables those pages are rarely in memory, so each insert reads one
/// and dirties it. <c>TimeOrderedIds.New()</c> gives a version 7 id instead, which lands on the growing end of each index.
/// EF Core needs no help (the provider generates version 7 ids for unset keys); the raw SQL writers, and the code that
/// builds their rows, assign ids themselves, and one <c>Guid.NewGuid()</c> among them undoes the rest.
/// </para>
/// <para>
/// The sweep covers the projects that build those rows: the models, the application layer, the repositories and the
/// Worker. Every <c>Guid.NewGuid()</c> there must either become <c>TimeOrderedIds.New()</c> or carry a
/// <c>// random-id: &lt;reason&gt;</c> comment on its line or one of the three above it, saying why the value must stay
/// random: a credential or anything else that must be unguessable, or a value that is never a row's id (a correlation
/// id, an in-memory owner, a preview object that is never written). Comments and string literals are not code and are
/// skipped. There is no test-side allowlist.
/// </para>
/// </remarks>
public class TimeOrderedIdConventionTests
{
    private const string Marker = "random-id:";

    /// <summary>
    /// The projects whose code builds the rows the raw SQL writers persist.
    /// </summary>
    private static readonly string[] SweptProjects =
    [
        "src/JIM.Models",
        "src/JIM.Data",
        "src/JIM.Application",
        "src/JIM.PostgresData",
        "src/JIM.InMemoryData",
        "src/JIM.Worker"
    ];

    private static readonly Regex NewGuidCall = new(@"\bGuid\.NewGuid\(\)", RegexOptions.Compiled);

    [Test]
    public void ProductionCode_RandomIds_EachCarryAReason()
    {
        var root = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "JIM.sln")))
            root = root.Parent;
        Assert.That(root, Is.Not.Null, "Could not locate repository sources for the random id sweep.");

        var failures = new List<string>();
        var fileCount = 0;
        foreach (var project in SweptProjects)
        {
            var directory = Path.Combine(root!.FullName, project);
            Assert.That(Directory.Exists(directory), Is.True, $"{project} must exist for the sweep to cover it.");

            foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
                         .Where(f => !IsGenerated(f)))
            {
                fileCount++;
                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    if (!NewGuidCall.Matches(lines[i]).Any(match => IsCode(lines[i], match.Index)))
                        continue;

                    var context = lines.Skip(Math.Max(0, i - 3)).Take(Math.Min(i, 3) + 1);
                    if (context.Any(line => line.Contains("// " + Marker, StringComparison.Ordinal)))
                        continue;

                    failures.Add($"{Path.GetRelativePath(root.FullName, file)}:{i + 1}: use TimeOrderedIds.New() for a row's id, " +
                                 $"or say why the value must stay random with a '// {Marker} <reason>' comment");
                }
            }
        }

        Assert.That(fileCount, Is.GreaterThan(0), "The sweep must find source files.");
        Assert.That(failures, Is.Empty, string.Join(Environment.NewLine, failures));
    }

    [TestCase("var id = Guid.NewGuid();", true)]
    [TestCase("// var id = Guid.NewGuid();", false)]
    [TestCase("/// <c>Guid.NewGuid()</c> is detected.", false)]
    [TestCase("return \"Guid.NewGuid()\";", false)]
    [TestCase("Log(\"a \\\"quoted\\\" word\", Guid.NewGuid());", true)]
    [TestCase("var id = Guid.NewGuid(); // a trailing comment", true)]
    [TestCase("var name = $\"{host}-{Guid.NewGuid():N}\";", true)]
    [TestCase("var text = $\"{{literal}} Guid.NewGuid() {count}\";", false)]
    public void IsCode_Line_TellsCodeFromCommentsAndStrings(string line, bool expected)
    {
        var match = NewGuidCall.Match(line);

        Assert.That(IsCode(line, match.Index), Is.EqualTo(expected));
    }

    private static bool IsGenerated(string file) =>
        file.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    /// <summary>
    /// Whether the text at <paramref name="index"/> is code: not after a line comment, and not inside a string literal,
    /// though inside an interpolated string's braces it is code again.
    /// </summary>
    private static bool IsCode(string line, int index)
    {
        var inString = false;
        var interpolated = false;
        var holeDepth = 0;
        for (var i = 0; i < index; i++)
        {
            var c = line[i];
            if (inString)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == '"')
                {
                    inString = false;
                }
                else if (interpolated && c == '{')
                {
                    if (i + 1 < line.Length && line[i + 1] == '{')
                    {
                        i++;
                    }
                    else
                    {
                        inString = false;
                        holeDepth++;
                    }
                }
            }
            else if (c == '"')
            {
                inString = true;
                interpolated = i > 0 && line[i - 1] == '$';
            }
            else if (holeDepth > 0 && c == '}')
            {
                holeDepth--;
                inString = true;
                interpolated = true;
            }
            else if (c == '/' && i + 1 < line.Length && line[i + 1] == '/')
            {
                return false;
            }
        }

        return !inString;
    }
}
