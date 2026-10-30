// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Which Run Profile "Run now" queues for a Connected System's changes awaiting synchronisation (#2615): the Delta
/// Synchronisation, or the Full Synchronisation where there is none. The caption and the dialog both choose through it.
/// </summary>
[TestFixture]
public class AwaitingSynchronisationRunProfileTests
{
    private static ConnectedSystemRunProfile Profile(int id, ConnectedSystemRunType runType) => new() { Id = id, Name = $"Profile {id}", RunType = runType };

    [Test]
    public void Preferred_DeltaAndFullSynchronisation_ChoosesTheDeltaSynchronisation()
    {
        var delta = Profile(2, ConnectedSystemRunType.DeltaSynchronisation);

        var chosen = AwaitingSynchronisationRunProfile.Preferred([Profile(1, ConnectedSystemRunType.FullSynchronisation), delta]);

        Assert.That(chosen, Is.SameAs(delta));
    }

    [Test]
    public void Preferred_OnlyFullSynchronisation_ChoosesIt()
    {
        var full = Profile(1, ConnectedSystemRunType.FullSynchronisation);

        var chosen = AwaitingSynchronisationRunProfile.Preferred([Profile(2, ConnectedSystemRunType.FullImport), full]);

        Assert.That(chosen, Is.SameAs(full));
    }

    [Test]
    public void Preferred_NeitherSynchronisation_ChoosesNothing()
    {
        var chosen = AwaitingSynchronisationRunProfile.Preferred(
            [Profile(1, ConnectedSystemRunType.FullImport), Profile(2, ConnectedSystemRunType.Export)]);

        Assert.That(chosen, Is.Null);
    }

    [Test]
    public void Preferred_NoRunProfiles_ChoosesNothing()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(AwaitingSynchronisationRunProfile.Preferred([]), Is.Null);
            Assert.That(AwaitingSynchronisationRunProfile.Preferred(null), Is.Null);
        }
    }

    [Test]
    public void Preferred_SeveralDeltaSynchronisations_ChoosesTheEarliestCreated()
    {
        var first = Profile(3, ConnectedSystemRunType.DeltaSynchronisation);

        var chosen = AwaitingSynchronisationRunProfile.Preferred(
            [Profile(9, ConnectedSystemRunType.DeltaSynchronisation), first, Profile(5, ConnectedSystemRunType.DeltaSynchronisation)]);

        Assert.That(chosen, Is.SameAs(first), "the choice does not move as more Run Profiles are added");
    }
}
