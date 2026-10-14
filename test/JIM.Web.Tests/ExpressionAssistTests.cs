// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Web.Models;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// <see cref="ExpressionAssist"/> (#2053, PRD Phase 2 requirement 9): what the expression editor suggests at the caret
/// (functions from the catalogue, attributes inside an accessor) and which function's signature it explains.
/// </summary>
[TestFixture]
public class ExpressionAssistTests
{
    private static readonly string[] Metaverse = ["Display Name", "Email", "Employee Status"];
    private static readonly string[] ConnectedSystem = ["mail", "proxyAddresses", "sAMAccountName"];

    private static ExpressionCompletion Complete(string textWithCaret)
    {
        var caret = textWithCaret.IndexOf('|', StringComparison.Ordinal);
        var text = textWithCaret.Remove(caret, 1);
        return ExpressionAssist.Complete(text, caret, Metaverse, ConnectedSystem);
    }

    private static ExpressionSignature? Signature(string textWithCaret)
    {
        var caret = textWithCaret.IndexOf('|', StringComparison.Ordinal);
        return ExpressionAssist.SignatureAt(textWithCaret.Remove(caret, 1), caret);
    }

    #region Functions

    [Test]
    public void Complete_APartFunctionName_SuggestsTheFunctionsItStarts()
    {
        var completion = Complete("Sta|");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(completion.Suggestions.Select(s => s.Label), Is.EqualTo(new[] { "StartsWith" }));
            Assert.That(completion.Suggestions[0].Kind, Is.EqualTo(ExpressionSuggestionKind.Function));
            Assert.That(completion.Suggestions[0].InsertText, Is.EqualTo("StartsWith("));
            Assert.That((completion.ReplaceStart, completion.ReplaceEnd), Is.EqualTo((0, 3)));
        }
    }

    [Test]
    public void Complete_IgnoresCaseInWhatIsTyped()
    {
        // "lo" is also inside IsNullOrEmpty and IsNullOrWhitespace, which follow: a name starting with it comes first.
        Assert.That(Complete("lo|").Suggestions[0].Label, Is.EqualTo("Lower"));
    }

    [Test]
    public void Complete_AfterAnOperator_SuggestsFromTheWordAtTheCaret()
    {
        var completion = Complete("\"x\" + Up|");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(completion.Suggestions.Select(s => s.Label), Is.EqualTo(new[] { "Upper" }));
            Assert.That((completion.ReplaceStart, completion.ReplaceEnd), Is.EqualTo((6, 8)));
        }
    }

    [Test]
    public void Complete_FunctionsThatStartWithTheWordComeBeforeThoseThatContainIt()
    {
        Assert.That(Complete("Rep|").Suggestions.Select(s => s.Label), Is.EqualTo(new[] { "Replace", "RegexReplace" }));
    }

    [Test]
    public void Complete_AWholeFunctionNameAlreadyCalled_SuggestsNothing()
    {
        Assert.That(Complete("Lower|(").Suggestions, Is.Empty);
    }

    [TestCase("\"Sta|")]
    [TestCase("cs[\"mail\"].Sta|")]
    [TestCase("|")]
    [TestCase("Lower(cs[\"mail\"]) |")]
    public void Complete_NowhereAFunctionNameIsBeingTyped_SuggestsNothing(string textWithCaret)
    {
        Assert.That(Complete(textWithCaret).Suggestions, Is.Empty);
    }

    [Test]
    public void Complete_AFunctionSuggestion_CarriesItsSignatureAndDescription()
    {
        var suggestion = Complete("IsMat|").Suggestions.Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(suggestion.Detail, Is.EqualTo("IsMatch(value, pattern[, ignoreCase])"));
            Assert.That(suggestion.Description, Does.StartWith("Check if text matches a regular expression"));
            Assert.That(suggestion.Description, Does.Not.Contain("]("), "Markdown links are read as their text");
        }
    }

    #endregion

    #region Attributes

    [Test]
    public void Complete_InsideAConnectedSystemAccessor_SuggestsItsAttributes()
    {
        var completion = Complete("Lower(cs[\"ma|");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(completion.Suggestions.Select(s => s.Label), Is.EqualTo(new[] { "mail", "sAMAccountName" }));
            Assert.That(completion.Suggestions[0].Kind, Is.EqualTo(ExpressionSuggestionKind.ConnectedSystemAttribute));
            Assert.That(completion.Suggestions[0].InsertText, Is.EqualTo("mail\"]"));
            Assert.That((completion.ReplaceStart, completion.ReplaceEnd), Is.EqualTo((10, 12)));
        }
    }

    [Test]
    public void Complete_InsideAMetaverseAccessor_SuggestsItsAttributes()
    {
        Assert.That(Complete("mv[\"E|").Suggestions.Select(s => s.Label), Is.EqualTo(new[] { "Email", "Employee Status", "Display Name" }));
    }

    [Test]
    public void Complete_InsideAnEmptyAccessor_SuggestsEveryAttribute()
    {
        Assert.That(Complete("mv[\"|").Suggestions, Has.Count.EqualTo(Metaverse.Length));
    }

    [Test]
    public void Complete_AnAccessorAlreadyClosed_InsertsOnlyTheName()
    {
        var completion = Complete("cs[\"ma|\"]");

        Assert.That(completion.Suggestions[0].InsertText, Is.EqualTo("mail"));
    }

    [Test]
    public void Complete_AnAttributeWithAQuoteInItsName_IsEscaped()
    {
        var completion = ExpressionAssist.Complete("cs[\"O", 5, [], ["O\"Brien"]);

        Assert.That(completion.Suggestions[0].InsertText, Is.EqualTo("O\\\"Brien\"]"));
    }

    #endregion

    #region Signature help

    [Test]
    public void SignatureAt_InsideACall_IsThatFunctionAtTheFirstParameter()
    {
        var signature = Signature("StartsWith(|");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(signature?.Function.Name, Is.EqualTo("StartsWith"));
            Assert.That(signature?.ParameterIndex, Is.EqualTo(0));
        }
    }

    [Test]
    public void SignatureAt_AfterACommaAtTheCallsLevel_IsTheNextParameter()
    {
        Assert.That(Signature("StartsWith(cs[\"mail\"], |")?.ParameterIndex, Is.EqualTo(1));
    }

    [Test]
    public void SignatureAt_CommasInsideStringsAndInnerCalls_DoNotCount()
    {
        var signature = Signature("Replace(Join(cs[\"x\"], \",\"), \"a,b\", |");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(signature?.Function.Name, Is.EqualTo("Replace"));
            Assert.That(signature?.ParameterIndex, Is.EqualTo(2));
        }
    }

    [Test]
    public void SignatureAt_InsideAnInnerCall_IsTheInnerFunction()
    {
        Assert.That(Signature("Lower(Trim(|")?.Function.Name, Is.EqualTo("Trim"));
    }

    [Test]
    public void SignatureAt_AfterTheCallCloses_IsTheEnclosingCall()
    {
        Assert.That(Signature("IIF(HasValue(cs[\"x\"]), |")?.Function.Name, Is.EqualTo("IIF"));
    }

    [TestCase("|")]
    [TestCase("Lower(cs[\"mail\"])|")]
    [TestCase("(1 + |")]
    [TestCase("Unknown(|")]
    public void SignatureAt_NotInsideAKnownCall_IsNothing(string textWithCaret)
    {
        Assert.That(Signature(textWithCaret), Is.Null);
    }

    [Test]
    public void SignatureAt_PastTheLastParameter_StaysOnTheLast()
    {
        Assert.That(Signature("Lower(a, b, |")?.ParameterIndex, Is.EqualTo(0));
    }

    #endregion
}
