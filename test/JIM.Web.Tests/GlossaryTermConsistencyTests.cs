// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Web;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Guards <see cref="TermDefinitions"/> against drifting from docs/reference/glossary.md (#1670): every
/// <c>TermHint</c> shows a definition copied verbatim from the glossary, so a rewording on one side that
/// misses the other would leave the portal and the docs disagreeing about what a term means. Reads the
/// real glossary file from the repository rather than a fixture, so an edit to the page itself is what
/// this test checks against.
/// </summary>
[TestFixture]
public class GlossaryTermConsistencyTests
{
    private static readonly Lazy<string> RepositoryRoot = new(FindRepositoryRoot);
    private static readonly Lazy<string> GlossaryContent = new(ReadGlossary);

    [TestCaseSource(nameof(AllTerms))]
    public void TermDefinition_TextAppearsVerbatimInGlossary(Term term)
    {
        var entry = TermDefinitions.For(term);

        Assert.That(GlossaryContent.Value, Does.Contain(entry.Text),
            $"Expected the glossary to contain the exact text TermHint shows for {term}. " +
            "Either the glossary entry was reworded without updating TermDefinitions, or vice versa.");
    }

    [TestCaseSource(nameof(AllTerms))]
    public void TermDefinition_AnchorExistsInGlossary(Term term)
    {
        var entry = TermDefinitions.For(term);

        Assert.That(GlossaryContent.Value, Does.Contain($"{{ #{entry.GlossaryAnchor} }}"),
            $"Expected the glossary to declare the anchor '{entry.GlossaryAnchor}' TermHint links to for {term}.");
    }

    private static IEnumerable<Term> AllTerms() => Enum.GetValues<Term>();

    private static string ReadGlossary()
    {
        var path = Path.Join(RepositoryRoot.Value, "docs", "reference", "glossary.md");
        Assert.That(File.Exists(path), Is.True, $"Expected the glossary at '{path}'.");
        return File.ReadAllText(path);
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
