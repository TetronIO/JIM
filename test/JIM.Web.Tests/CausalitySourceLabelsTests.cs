// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Web.Causality;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The synthetic source row opens all three causality views and is the first thing read on the panel, so it
/// was the most visible row to keep saying "record" while the technical-names toggle was on. All three views
/// now name it from here.
/// </summary>
[TestFixture]
public class CausalitySourceLabelsTests
{
    [Test]
    public void Title_PlainLanguage_CallsItASourceRecord()
    {
        Assert.That(CausalitySourceLabels.Title(technicalNames: false), Is.EqualTo("Source record"));
    }

    [Test]
    public void Title_TechnicalNames_CallsItAConnectedSystemObject()
    {
        Assert.That(CausalitySourceLabels.Title(technicalNames: true), Is.EqualTo("Connected System Object"));
    }

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

    [Test]
    public void Title_TechnicalNames_FitsTheGraphsTitleCap()
    {
        // The Graph truncates node titles, and a title truncated to "Connected System Object..." with the
        // ellipsis eating the last word would read as a different term rather than a shortened one.
        Assert.That(CausalitySourceLabels.Title(technicalNames: true),
            Has.Length.LessThanOrEqualTo(CausalityGraphLayoutCalculator.TitleMaxLength));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void NeitherLabel_UsesThePlainWordRecordWhenTechnicalNamesAreOn(bool technicalNames)
    {
        var usesPlainVocabulary = CausalitySourceLabels.Title(technicalNames).Contains("record")
                                  || CausalitySourceLabels.Verb(technicalNames).Contains("Record");

        Assert.That(usesPlainVocabulary, Is.EqualTo(!technicalNames),
            "the toggle governs this row exactly as it governs every other one");
    }
}
