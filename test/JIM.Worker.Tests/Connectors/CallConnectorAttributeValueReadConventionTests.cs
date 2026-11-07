// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.RegularExpressions;
using NUnit.Framework;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// The call-based Connectors (those implementing <c>IConnectorExportUsingCalls</c>) are handed objects whose large
/// multi-valued reference attributes may be loaded in part (#2816): a group's member attribute holds only the members the
/// export's changes name. A Connector that reads a group's members straight from <c>AttributeValues</c> would quietly get
/// that subset. So every direct read of <c>AttributeValues</c> in their code must either go through the object's
/// held-value helpers (<c>HeldValueCount</c>, <c>HoldsAnyValue</c>, <c>HeldValuesOf</c>, which answer from the held
/// count or refuse a partial attribute), or carry a <c>lean-load-safe:</c> marker comment saying why the attribute it
/// reads is never loaded in part (an identifier, a single-valued or non-reference attribute).
/// </summary>
/// <remarks>
/// Which attribute a read filters to is runtime data (a name such as <c>member</c>, an id), so a source sweep cannot tell
/// a member read from an identifier read. The sweep is therefore stricter than the rule it guards: it flags every direct
/// read, and the marker records the author's reason where the next reader will see it. String literals and comments are
/// ignored, so a log message naming the property is not a read.
/// </remarks>
[TestFixture]
public class CallConnectorAttributeValueReadConventionTests
{
    private const string Marker = "lean-load-safe:";

    private static readonly Regex Read = new(@"\bAttributeValues\b", RegexOptions.Compiled);
    private static readonly Regex StringLiteral = new(@"@?""(?:[^""\\]|\\.)*""", RegexOptions.Compiled);

    private static string RepositoryRoot()
    {
        var root = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "JIM.sln")))
            root = root.Parent;
        Assert.That(root, Is.Not.Null, "the repository root holds JIM.sln");
        return root!.FullName;
    }

    private static readonly string[] SweptDirectories = ["LDAP", "Sql", "SCIM", "Mock"];

    [Test]
    public void CallConnectors_ReadAttributeValuesOnlyThroughTheHeldValueHelpersOrWithAReason()
    {
        var connectors = Path.Combine(RepositoryRoot(), "src", "JIM.Connectors");
        var failures = new List<string>();

        foreach (var file in SweptDirectories.SelectMany(directory =>
                     Directory.GetFiles(Path.Combine(connectors, directory), "*.cs", SearchOption.AllDirectories)))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var code = CodeOf(lines[i]);
                if (!Read.IsMatch(code) || IsMarked(lines, i))
                    continue;

                failures.Add($"{Path.GetRelativePath(connectors, file)}:{i + 1}: {lines[i].Trim()}");
            }
        }

        Assert.That(failures, Is.Empty,
            "A call-based Connector reads AttributeValues directly. An object's large member attribute may be loaded in part " +
            "(#2816), so read what it holds through HeldValueCount, HoldsAnyValue or HeldValuesOf, or, where the attribute read " +
            $"is never loaded in part (an identifier, a single-valued or non-reference attribute), say so with a '// {Marker} <reason>' " +
            "comment on the line or the line above:" + Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    /// <summary>
    /// Every Connector that exports by calls is swept: a new one in another folder would otherwise escape the rule.
    /// </summary>
    [Test]
    public void CallConnectors_AreAllSwept()
    {
        var connectors = Path.Combine(RepositoryRoot(), "src", "JIM.Connectors");
        var implementing = Directory.GetFiles(connectors, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(file => File.ReadAllText(file).Contains("IConnectorExportUsingCalls"))
            .Select(file => Path.GetRelativePath(connectors, file).Split(Path.DirectorySeparatorChar)[0])
            .Distinct()
            .ToList();

        Assert.That(implementing, Is.SubsetOf(SweptDirectories),
            "a Connector exporting by calls lives in a folder this sweep does not cover; add the folder to SweptDirectories");
    }

    private static string CodeOf(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("*", StringComparison.Ordinal))
            return string.Empty;

        var code = StringLiteral.Replace(line, "\"\"");
        var comment = code.IndexOf("//", StringComparison.Ordinal);
        return comment >= 0 ? code[..comment] : code;
    }

    private static bool IsMarked(string[] lines, int index) =>
        lines[index].Contains(Marker, StringComparison.Ordinal) ||
        (index > 0 && lines[index - 1].TrimStart().StartsWith("//", StringComparison.Ordinal) && lines[index - 1].Contains(Marker, StringComparison.Ordinal));
}
