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
}
