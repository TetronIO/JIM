// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Web.Causality;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The synthetic source row opens the Timeline and is among the first things read on the panel, so
/// its wording is held to the same one-vocabulary rule as every other row.
/// </summary>
[TestFixture]
public class CausalitySourceLabelsTests
{
    [Test]
    public void Verb_ReadsAsASentenceInThePortalsVocabulary()
    {
        Assert.That(CausalitySourceLabels.Verb(), Is.EqualTo("Connected System Object processed"));
    }

    [Test]
    public void Verb_NeverSaysRecord()
    {
        Assert.That(CausalitySourceLabels.Verb(), Does.Not.Contain("record").IgnoreCase);
    }

    [Test]
    public void Verb_Speculative_ReadsInTheConditionalMood()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(CausalitySourceLabels.Verb(isSpeculative: true), Is.EqualTo("Connected System Object would be processed"));
            Assert.That(CausalitySourceLabels.Verb(isSpeculative: false), Is.EqualTo("Connected System Object processed"),
                "A recorded run keeps its past-tense verb");
        }
    }
}
