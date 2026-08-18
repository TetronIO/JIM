// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Expressions;
using JIM.Models.Expressions;
using NUnit.Framework;

namespace JIM.Worker.Tests.Expressions;

/// <summary>
/// Resolving the attributes an Expression reads, which is what lets the portal offer a sample value per input
/// rather than asking an administrator to assemble a request by hand.
/// </summary>
[TestFixture]
public class ExpressionInputResolverTests
{
    [Test]
    public void Resolve_MetaverseAndConnectedSystemAccessors_ReturnsBothWithTheirSide()
    {
        var inputs = ExpressionInputResolver.Resolve("mv[\"Display Name\"] + cs[\"employeeNumber\"]");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(inputs, Has.Count.EqualTo(2));
            Assert.That(inputs[0].Source, Is.EqualTo(ExpressionInputSource.Metaverse));
            Assert.That(inputs[0].AttributeName, Is.EqualTo("Display Name"));
            Assert.That(inputs[1].Source, Is.EqualTo(ExpressionInputSource.ConnectedSystem));
            Assert.That(inputs[1].AttributeName, Is.EqualTo("employeeNumber"));
        }
    }

    [Test]
    public void Resolve_TheSameAttributeTwice_ReturnsItOnce()
    {
        // A tester offering the same input twice would ask for the same value twice and then disagree with itself.
        var inputs = ExpressionInputResolver.Resolve("cs[\"givenName\"] + \" \" + cs[\"givenName\"]");

        Assert.That(inputs, Has.Count.EqualTo(1));
        Assert.That(inputs[0].AttributeName, Is.EqualTo("givenName"));
    }

    [Test]
    public void Resolve_SameNameOnBothSides_ReturnsBoth()
    {
        // mv["mail"] and cs["mail"] are different inputs holding different values; collapsing them would
        // silently test the Expression with one value where it reads two.
        var inputs = ExpressionInputResolver.Resolve("IIF(IsNullOrEmpty(cs[\"mail\"]), mv[\"mail\"], cs[\"mail\"])");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(inputs, Has.Count.EqualTo(2));
            Assert.That(inputs.Any(i => i.Source == ExpressionInputSource.ConnectedSystem && i.AttributeName == "mail"), Is.True);
            Assert.That(inputs.Any(i => i.Source == ExpressionInputSource.Metaverse && i.AttributeName == "mail"), Is.True);
        }
    }

    [Test]
    public void Resolve_AccessorsWrittenWithWhitespace_AreStillFound()
    {
        var inputs = ExpressionInputResolver.Resolve("mv [ \"Job Title\" ]");

        Assert.That(inputs, Has.Count.EqualTo(1));
        Assert.That(inputs[0].AttributeName, Is.EqualTo("Job Title"));
    }

    [Test]
    public void Resolve_AnAttributeNameContainingAnEscapedQuote_KeepsTheWholeName()
    {
        var inputs = ExpressionInputResolver.Resolve("cs[\"say \\\"hello\\\"\"]");

        Assert.That(inputs, Has.Count.EqualTo(1));
        Assert.That(inputs[0].AttributeName, Is.EqualTo("say \\\"hello\\\""));
    }

    [Test]
    public void Resolve_AnIdentifierMerelyEndingInMvOrCs_IsNotAnInput()
    {
        // "abcs" ends in "cs" but is not the accessor; a word-boundary-free match would invent an input.
        var inputs = ExpressionInputResolver.Resolve("abcs[\"nope\"] + xmv[\"alsoNope\"]");

        Assert.That(inputs, Is.Empty);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("\"a literal with no accessors\"")]
    public void Resolve_NothingToFind_ReturnsEmpty(string? expression)
    {
        Assert.That(ExpressionInputResolver.Resolve(expression), Is.Empty);
    }

    /// <summary>
    /// The synchronisation path resolves the same Expression once per object, so it takes the cached route. The
    /// cache must be a pure optimisation: same answer, repeatable, and the same empty result for nothing to find.
    /// </summary>
    [Test]
    public void ResolveCached_ReturnsTheSameInputsAsResolve_AndRepeats()
    {
        const string expression = "Lower(cs[\"givenName\"]) + \".\" + Lower(cs[\"sn\"]) + mv[\"Domain\"]";

        var uncached = ExpressionInputResolver.Resolve(expression);
        var first = ExpressionInputResolver.ResolveCached(expression);
        var second = ExpressionInputResolver.ResolveCached(expression);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first, Is.EqualTo(uncached));
            Assert.That(second, Is.EqualTo(uncached));
        }
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void ResolveCached_NothingToFind_ReturnsEmpty(string? expression)
    {
        Assert.That(ExpressionInputResolver.ResolveCached(expression), Is.Empty);
    }

    /// <summary>
    /// The three states that count as no value, and the one that does not. Both the inbound and the outbound
    /// paths ask this question, so it is answered in one place rather than twice.
    /// </summary>
    [Test]
    public void FindMissingInputs_AbsentNullAndEmptyCountAsNoValue_WhitespaceDoesNot()
    {
        var attributes = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["present"] = "Ada",
            ["nulled"] = null,
            ["empty"] = string.Empty,
            ["blank"] = "   "
        };

        var missing = ExpressionInputResolver.FindMissingInputs(
            "cs[\"present\"] + cs[\"nulled\"] + cs[\"empty\"] + cs[\"blank\"] + cs[\"absent\"]",
            ExpressionInputSource.ConnectedSystem,
            attributes);

        // Whitespace is not judged here: whether it counts as a value is the mapping's own "treat whitespace as
        // no value" setting, applied later, and second-guessing it here would override the administrator.
        Assert.That(missing, Is.EqualTo(new[] { "cs[\"nulled\"]", "cs[\"empty\"]", "cs[\"absent\"]" }));
    }

    [Test]
    public void FindMissingInputs_InputsFromTheOtherSide_AreLeftAlone()
    {
        // An inbound evaluation carries Connected System values only, so an mv[...] accessor is not an object
        // missing a value; it resolves to null by design and is the Expression author's business.
        var missing = ExpressionInputResolver.FindMissingInputs(
            "cs[\"sn\"] + mv[\"Display Name\"]",
            ExpressionInputSource.ConnectedSystem,
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["sn"] = "Lovelace" });

        Assert.That(missing, Is.Empty);
    }
}
