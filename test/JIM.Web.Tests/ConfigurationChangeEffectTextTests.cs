// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities.DTOs;
using JIM.Models.Staging.DTOs;
using JIM.Web.Models;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The logic inside the save acknowledgement's "When this takes effect" sentences (#2615): when a duration reads "under a
/// minute", what rounds to a whole number of minutes, and when the duration is left out. The wording around them is
/// presentation and is not pinned.
/// </summary>
[TestFixture]
public class ConfigurationChangeEffectTextTests
{
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(30)]
    [TestCase(59)]
    public void Duration_UnderAMinute_SaysSo(int seconds)
    {
        Assert.That(ConfigurationChangeEffectText.Duration(TimeSpan.FromSeconds(seconds)), Is.EqualTo("under a minute"));
    }

    [TestCase(60, "about 1 minute")]
    [TestCase(89, "about 1 minute")]
    [TestCase(90, "about 2 minutes")]
    [TestCase(28 * 60, "about 28 minutes")]
    [TestCase(28 * 60 + 29, "about 28 minutes")]
    [TestCase(28 * 60 + 30, "about 29 minutes")]
    [TestCase(125 * 60, "about 125 minutes")]
    [TestCase(1_500 * 60, "about 1,500 minutes")]
    public void Duration_AMinuteOrMore_RoundsToTheNearestWholeMinute(int seconds, string expected)
    {
        Assert.That(ConfigurationChangeEffectText.Duration(TimeSpan.FromSeconds(seconds)), Is.EqualTo(expected));
    }

    [Test]
    public void DurationClause_NoEstimate_IsEmpty()
    {
        Assert.That(ConfigurationChangeEffectText.DurationClause(null), Is.Empty, "no Full Synchronisation to take a speed from");
    }

    [Test]
    public void DurationClause_AnEstimate_CarriesTheDuration()
    {
        Assert.That(ConfigurationChangeEffectText.DurationClause(TimeSpan.FromMinutes(28)), Does.Contain("about 28 minutes"));
    }

    [Test]
    public void ProcessedInFull_OneObject_IsSingular()
    {
        var text = ConfigurationChangeEffectText.ProcessedInFull(new AwaitingSynchronisationEstimate
        {
            ConnectedSystemName = "HR", ObjectTypeName = "user", ObjectCount = 1
        });

        Assert.That(text, Does.Contain("1 object").And.Not.Contain("1 objects"));
    }

    [Test]
    public void FullSynchronisation_NamesTheSectionsAndTheSystem()
    {
        var text = ConfigurationChangeEffectText.FullSynchronisation(new ConfigurationChangeEffect
        {
            FullSynchronisationSections = ["Attribute Flow", "Enforce state"],
            FullSynchronisationConnectedSystemName = "Contoso AD"
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(text, Does.Contain("Attribute Flow").And.Contain("Enforce state"));
            Assert.That(text, Does.Contain("Contoso AD"));
        }
    }
}
