// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Web.Causality;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The synthetic source row opens the Timeline and is among the first things read on the panel, so
/// it was the most visible row to keep saying "record" while the technical-names toggle was on.
/// </summary>
[TestFixture]
public class CausalitySourceLabelsTests
{
    [Test]
    public void Verb_PlainLanguage_ReadsAsASentence()
    {
        Assert.That(CausalitySourceLabels.Verb(technicalNames: false), Is.EqualTo("Record processed"));
    }

    [Test]
    public void Verb_TechnicalNames_ReadsAsASentenceToo()
    {
        Assert.That(CausalitySourceLabels.Verb(technicalNames: true),
            Is.EqualTo("Connected System Object processed"));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void Verb_UsesThePlainWordRecordOnlyWhenTechnicalNamesAreOff(bool technicalNames)
    {
        Assert.That(CausalitySourceLabels.Verb(technicalNames).Contains("Record"),
            Is.EqualTo(!technicalNames),
            "the toggle governs this row exactly as it governs every other one");
    }
}
