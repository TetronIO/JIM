// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Logic;
using JIM.Models.Transactional;
using JIM.Web.Models;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The parts of the generated-value availability wording that logic decides (Unique Value Generation, #242, release 3):
/// which reasons carry a line at all, and how the Sync Preview probe note names one, two or more Connected Systems.
/// </summary>
[TestFixture]
public class GeneratedValueAvailabilityWordingTests
{
    [Test]
    public void ReasonText_None_HasNoLine()
    {
        Assert.That(GeneratedValueAvailabilityWording.ReasonText(GeneratedValueParticipantReason.None), Is.Null);
    }

    [Test]
    public void ReasonText_EveryOtherReason_HasALine()
    {
        var reasons = Enum.GetValues<GeneratedValueParticipantReason>().Where(r => r != GeneratedValueParticipantReason.None);

        Assert.That(reasons.Select(GeneratedValueAvailabilityWording.ReasonText), Has.None.Null);
    }

    [Test]
    public void CheckLabel_EachCheck_IsDistinct()
    {
        var labels = Enum.GetValues<GeneratedValueParticipantCheck>().Select(GeneratedValueAvailabilityWording.CheckLabel).ToList();

        Assert.That(labels, Is.Unique);
    }

    [TestCase(new[] { "Corp AD" }, "probes Corp AD.", "If it already")]
    [TestCase(new[] { "Corp AD", "OpenLDAP" }, "probes Corp AD and OpenLDAP.", "If either already")]
    [TestCase(new[] { "Corp AD", "Mail", "OpenLDAP" }, "probes Corp AD, Mail and OpenLDAP.", "If any of them already")]
    public void ProbeNote_NamesTheSystemsAndPicksThePronounByCount(string[] names, string expectedSystems, string expectedPronoun)
    {
        var (beforeValue, value, _) = GeneratedValueAvailabilityWording.ProbeNote(
            new SyncPreviewGeneratedValueProbe { AttributeName = "Account Name", Value = "jbloggs", ConnectedSystemNames = [.. names] });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(beforeValue, Does.Contain(expectedSystems));
            Assert.That(beforeValue, Does.Contain(expectedPronoun));
            Assert.That(value, Is.EqualTo("jbloggs"));
        }
    }
}
