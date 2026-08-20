// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers.Preview;
using JIM.Models.Activities;
using JIM.Models.Preview;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Covers the summariser's grouping decisions (#827 Phase 4a). The rule the whole class exists to protect is that
/// **group counts are exact**: how a group is described, and how many of its delta rows were kept, must never move
/// the number of objects it reports.
/// </summary>
[TestFixture]
public class PreviewSummariserTests
{
    private static readonly Dictionary<int, string> NoConnectedSystems = [];

    private static PreviewDelta Delta(string? oldValue, string? newValue, string displayName = "Object",
        string attributeName = "Email") =>
        new(ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow,
            ObjectDisplayName: displayName,
            ObjectTypeName: "User",
            MetaverseObjectTypeId: 1,
            MetaverseObjectId: Guid.CreateVersion7(),
            AttributeName: attributeName,
            OldValue: oldValue,
            NewValue: newValue);

    [Test]
    public void BuildGroups_DistinctValuePairsWithinGuard_SplitsIntoOneGroupPerPair()
    {
        var summariser = new PreviewSummariser(maximumDeltasPerGroup: null);
        for (var i = 0; i < 5; i++)
            summariser.Add(Delta("@contoso.com", "@fabrikam.com"));
        for (var i = 0; i < 2; i++)
            summariser.Add(Delta("@contoso.co.uk", "@fabrikam.co.uk"));

        var groups = summariser.BuildGroups(Guid.CreateVersion7(), NoConnectedSystems);

        Assert.That(groups, Has.Count.EqualTo(2), "Two distinct value pairs should be named separately, not merged into one attribute-level group.");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(groups[0].OldValue, Is.EqualTo("@contoso.com"));
            Assert.That(groups[0].NewValue, Is.EqualTo("@fabrikam.com"));
            Assert.That(groups[0].ObjectCount, Is.EqualTo(5));
            Assert.That(groups[1].OldValue, Is.EqualTo("@contoso.co.uk"));
            Assert.That(groups[1].NewValue, Is.EqualTo("@fabrikam.co.uk"));
            Assert.That(groups[1].ObjectCount, Is.EqualTo(2));
            Assert.That(groups.Sum(g => g.Deltas.Count), Is.EqualTo(7), "With no cap, every delta should still be kept once and only once.");
        }
    }

    [Test]
    public void BuildGroups_SingleValuePair_IsStillNamed()
    {
        var summariser = new PreviewSummariser(maximumDeltasPerGroup: null);
        for (var i = 0; i < 3; i++)
            summariser.Add(Delta("@contoso.com", "@fabrikam.com"));

        var groups = summariser.BuildGroups(Guid.CreateVersion7(), NoConnectedSystems);

        Assert.That(groups, Has.Count.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            // The whole point of the domain-swap case: "Email: @contoso.com -> @fabrikam.com" reads better than
            // "Email changed", and it costs nothing to say when there is only one pair.
            Assert.That(groups[0].OldValue, Is.EqualTo("@contoso.com"));
            Assert.That(groups[0].NewValue, Is.EqualTo("@fabrikam.com"));
            Assert.That(groups[0].ObjectCount, Is.EqualTo(3));
        }
    }

    [Test]
    public void BuildGroups_ValuePairsBeyondGuard_CollapseToOneAttributeGroupWithExactCount()
    {
        var summariser = new PreviewSummariser(maximumDeltasPerGroup: null, maximumValuePairsPerGroup: 3);
        // Four distinct pairs against a guard of three: one too many to be a summary.
        for (var i = 0; i < 4; i++)
            summariser.Add(Delta($"old{i}", $"new{i}"));

        var groups = summariser.BuildGroups(Guid.CreateVersion7(), NoConnectedSystems);

        Assert.That(groups, Has.Count.EqualTo(1), "Past the guard the pairs stop being a summary and collapse into the attribute-level group.");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(groups[0].OldValue, Is.Null, "A collapsed group covers many values, so naming one of them would be a lie.");
            Assert.That(groups[0].NewValue, Is.Null);
            Assert.That(groups[0].AttributeName, Is.EqualTo("Email"));
            Assert.That(groups[0].ObjectCount, Is.EqualTo(4), "Collapsing changes how the population is described, never how many objects are in it.");
            Assert.That(groups[0].Deltas, Has.Count.EqualTo(4));
        }
    }

    [Test]
    public void BuildGroups_ValuePairsExactlyAtGuard_StillSplit()
    {
        var summariser = new PreviewSummariser(maximumDeltasPerGroup: null, maximumValuePairsPerGroup: 3);
        for (var i = 0; i < 3; i++)
            summariser.Add(Delta($"old{i}", $"new{i}"));

        var groups = summariser.BuildGroups(Guid.CreateVersion7(), NoConnectedSystems);

        Assert.That(groups, Has.Count.EqualTo(3), "The guard is a maximum, not a limit one short of it.");
    }

    [Test]
    public void BuildGroups_SplitGroups_ReportExactCountsWhenDeltasAreCapped()
    {
        var summariser = new PreviewSummariser(maximumDeltasPerGroup: 2);
        for (var i = 0; i < 30; i++)
            summariser.Add(Delta("@contoso.com", "@fabrikam.com"));
        for (var i = 0; i < 20; i++)
            summariser.Add(Delta("@contoso.co.uk", "@fabrikam.co.uk"));

        var groups = summariser.BuildGroups(Guid.CreateVersion7(), NoConnectedSystems);

        Assert.That(groups, Has.Count.EqualTo(2));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(groups[0].ObjectCount, Is.EqualTo(30));
            Assert.That(groups[1].ObjectCount, Is.EqualTo(20));
            Assert.That(groups.Sum(g => g.ObjectCount), Is.EqualTo(50), "Every delta must be counted in exactly one group, capped or not.");
            Assert.That(groups[0].Deltas, Has.Count.LessThan(groups[0].ObjectCount));
            Assert.That(groups[1].Deltas, Has.Count.LessThan(groups[1].ObjectCount));
            Assert.That(groups[0].DeltasSampled, Is.True);
            Assert.That(groups[1].DeltasSampled, Is.True);
        }
    }

    [Test]
    public void BuildGroups_ValueSortedStreamAndTightCap_StillGivesEveryGroupSomethingToDrillInto()
    {
        // An adapter that enumerates per container yields all of one value pair before any of the next. The kept
        // rows are a single attribute-level pool, so without a per-pair reserve every group after the first would
        // render an "objects: 200" row that drills down to nothing.
        var summariser = new PreviewSummariser(maximumDeltasPerGroup: 3);
        for (var i = 0; i < 20; i++)
            summariser.Add(Delta("OU=Sales", "OU=Trading"));
        for (var i = 0; i < 20; i++)
            summariser.Add(Delta("OU=Support", "OU=Service"));

        var groups = summariser.BuildGroups(Guid.CreateVersion7(), NoConnectedSystems);

        Assert.That(groups, Has.Count.EqualTo(2));
        Assert.That(groups.Where(g => g.Deltas.Count == 0), Is.Empty,
            "A group an administrator can see is a group they must be able to drill into.");
    }

    [Test]
    public void BuildGroups_GroupWhoseReserveCoversItEntirely_IsNotLabelledSampled()
    {
        // The small group falls entirely outside the capped pool, so its own reserve supplies every one of its
        // rows. "Sampled" describes what an administrator is looking at, not how it got there, and labelling a
        // complete list as a sample would have them assume objects are missing that are not.
        var summariser = new PreviewSummariser(maximumDeltasPerGroup: 2);
        for (var i = 0; i < 30; i++)
            summariser.Add(Delta("@contoso.com", "@fabrikam.com"));
        for (var i = 0; i < 4; i++)
            summariser.Add(Delta("@contoso.co.uk", "@fabrikam.co.uk"));

        var groups = summariser.BuildGroups(Guid.CreateVersion7(), NoConnectedSystems);

        var small = groups.Single(g => g.OldValue == "@contoso.co.uk");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(small.Deltas, Has.Count.EqualTo(4));
            Assert.That(small.DeltasSampled, Is.False);
        }
    }

    [Test]
    public void BuildGroups_DeltasKeptForASplitGroup_AllBelongToThatGroup()
    {
        var summariser = new PreviewSummariser(maximumDeltasPerGroup: null);
        for (var i = 0; i < 4; i++)
            summariser.Add(Delta("@contoso.com", "@fabrikam.com"));
        for (var i = 0; i < 4; i++)
            summariser.Add(Delta("@contoso.co.uk", "@fabrikam.co.uk"));

        var groups = summariser.BuildGroups(Guid.CreateVersion7(), NoConnectedSystems);

        using (Assert.EnterMultipleScope())
        {
            foreach (var group in groups)
                Assert.That(group.Deltas.All(d => d.OldValue == group.OldValue && d.NewValue == group.NewValue), Is.True,
                    "A drill-down that shows rows from a different value pair contradicts the row that was clicked.");
        }
    }

    [Test]
    public void BuildGroups_ValuesDifferingOnlyByCase_AreDistinctPairs()
    {
        var summariser = new PreviewSummariser(maximumDeltasPerGroup: null);
        summariser.Add(Delta("Smith", "SMITH"));
        summariser.Add(Delta("smith", "SMITH"));

        var groups = summariser.BuildGroups(Guid.CreateVersion7(), NoConnectedSystems);

        // A casing change is a real change, and Phase 4b detects it as a pattern; merging the two here would make
        // that undetectable.
        Assert.That(groups, Has.Count.EqualTo(2));
    }

    [Test]
    public void BuildGroups_DeltasWithNoValues_ProduceOneGroupWithNoValuesNamed()
    {
        var summariser = new PreviewSummariser(maximumDeltasPerGroup: null);
        for (var i = 0; i < 3; i++)
            summariser.Add(new PreviewDelta(ActivityRunProfileExecutionItemSyncOutcomeType.WouldFallOutOfScope,
                ObjectDisplayName: "Object", ObjectTypeName: "User", MetaverseObjectTypeId: 1));

        var groups = summariser.BuildGroups(Guid.CreateVersion7(), NoConnectedSystems);

        Assert.That(groups, Has.Count.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(groups[0].OldValue, Is.Null);
            Assert.That(groups[0].NewValue, Is.Null);
            Assert.That(groups[0].ObjectCount, Is.EqualTo(3));
        }
    }

    [Test]
    public void BuildGroups_EqualSizedValuePairGroups_AreOrderedDeterministically()
    {
        var first = new PreviewSummariser(maximumDeltasPerGroup: null);
        first.Add(Delta("b", "z"));
        first.Add(Delta("a", "y"));
        first.Add(Delta("c", "x"));

        var second = new PreviewSummariser(maximumDeltasPerGroup: null);
        second.Add(Delta("c", "x"));
        second.Add(Delta("b", "z"));
        second.Add(Delta("a", "y"));

        var firstGroups = first.BuildGroups(Guid.CreateVersion7(), NoConnectedSystems);
        var secondGroups = second.BuildGroups(Guid.CreateVersion7(), NoConnectedSystems);

        // The same preview re-read must not look like a different preview.
        Assert.That(firstGroups.Select(g => g.OldValue), Is.EqualTo(secondGroups.Select(g => g.OldValue)).AsCollection);
    }

    [Test]
    public void BuildGroups_DifferentAttributes_GuardIsCountedPerAttributeGroup()
    {
        var summariser = new PreviewSummariser(maximumDeltasPerGroup: null, maximumValuePairsPerGroup: 2);
        summariser.Add(Delta("a", "b", attributeName: "Email"));
        summariser.Add(Delta("c", "d", attributeName: "Email"));
        summariser.Add(Delta("e", "f", attributeName: "Department"));
        summariser.Add(Delta("g", "h", attributeName: "Department"));

        var groups = summariser.BuildGroups(Guid.CreateVersion7(), NoConnectedSystems);

        // Four pairs in total, two per attribute: neither attribute is over its own guard, so nothing collapses.
        Assert.That(groups, Has.Count.EqualTo(4));
        Assert.That(groups.Where(g => g.OldValue == null), Is.Empty);
    }

    [Test]
    public void Constructor_NonPositiveValuePairGuard_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = new PreviewSummariser(maximumDeltasPerGroup: null, maximumValuePairsPerGroup: 0));
    }

    #region Pattern detection (Phase 4b)

    [Test]
    public void BuildGroups_ValuePairGroup_CarriesThePatternItsValuesDescribe()
    {
        var summariser = new PreviewSummariser(maximumDeltasPerGroup: null);
        summariser.Add(Delta("bob@contoso.com", "bob@fabrikam.com"));
        summariser.Add(Delta("bob@contoso.com", "bob@fabrikam.com"));

        var groups = summariser.BuildGroups(Guid.CreateVersion7(), NoConnectedSystems);

        Assert.That(groups, Has.Count.EqualTo(1));
        Assert.That(groups[0].PatternKey, Is.EqualTo(PreviewPatternKeys.EmailDomainChanged));
    }

    [Test]
    public void BuildGroups_ValuePairGroup_LabelsItsKeptDeltasToo()
    {
        var summariser = new PreviewSummariser(maximumDeltasPerGroup: null);
        summariser.Add(Delta("bob@contoso.com", "bob@fabrikam.com"));

        var groups = summariser.BuildGroups(Guid.CreateVersion7(), NoConnectedSystems);

        Assert.That(groups[0].Deltas.Select(d => d.PatternKey),
            Is.All.EqualTo(PreviewPatternKeys.EmailDomainChanged));
    }

    [Test]
    public void BuildGroups_CollapsedGroupWhereEveryDeltaSharesAPattern_NamesIt()
    {
        // The case the detectors exist for: too many distinct domain pairs to name individually, but every one of
        // them is the same kind of change, and that is the sentence an administrator needs.
        var summariser = new PreviewSummariser(maximumDeltasPerGroup: null, maximumValuePairsPerGroup: 2);
        for (var i = 0; i < 6; i++)
            summariser.Add(Delta($"user{i}@contoso.com", $"user{i}@fabrikam.com"));

        var groups = summariser.BuildGroups(Guid.CreateVersion7(), NoConnectedSystems);

        Assert.That(groups, Has.Count.EqualTo(1), "the guard should have collapsed these back to the attribute level");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(groups[0].OldValue, Is.Null);
            Assert.That(groups[0].ObjectCount, Is.EqualTo(6));
            Assert.That(groups[0].PatternKey, Is.EqualTo(PreviewPatternKeys.EmailDomainChanged));
        }
    }

    [Test]
    public void BuildGroups_CollapsedGroupWhoseDeltasDisagree_NamesNoPattern()
    {
        var summariser = new PreviewSummariser(maximumDeltasPerGroup: null, maximumValuePairsPerGroup: 2);
        for (var i = 0; i < 5; i++)
            summariser.Add(Delta($"user{i}@contoso.com", $"user{i}@fabrikam.com"));
        summariser.Add(Delta("bsmith", "svc-bsmith"));

        var groups = summariser.BuildGroups(Guid.CreateVersion7(), NoConnectedSystems);

        Assert.That(groups[0].PatternKey, Is.Null,
            "five in six is not a pattern; a group labelled 'domain changed' must mean every object in it");
    }

    [Test]
    public void BuildGroups_CollapsedGroupWhereOneDeltaMatchesNothing_NamesNoPattern()
    {
        var summariser = new PreviewSummariser(maximumDeltasPerGroup: null, maximumValuePairsPerGroup: 2);
        for (var i = 0; i < 5; i++)
            summariser.Add(Delta($"user{i}@contoso.com", $"user{i}@fabrikam.com"));
        summariser.Add(Delta("Sales", "Marketing"));

        var groups = summariser.BuildGroups(Guid.CreateVersion7(), NoConnectedSystems);

        Assert.That(groups[0].PatternKey, Is.Null,
            "an unrecognised delta is a disagreement, not an abstention");
    }

    [Test]
    public void BuildGroups_CollapsedGroupThatDisagreedEarly_StillCountsEveryDelta()
    {
        // Consensus is abandoned as soon as it breaks, which is also when detection stops being worth running.
        // Whatever that short-circuit does, it must not touch the count.
        var summariser = new PreviewSummariser(maximumDeltasPerGroup: null, maximumValuePairsPerGroup: 1);
        summariser.Add(Delta("bob@contoso.com", "bob@fabrikam.com"));
        summariser.Add(Delta("Sales", "Marketing"));
        for (var i = 0; i < 20; i++)
            summariser.Add(Delta($"user{i}", $"svc-user{i}"));

        var groups = summariser.BuildGroups(Guid.CreateVersion7(), NoConnectedSystems);

        Assert.That(groups, Has.Count.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(groups[0].ObjectCount, Is.EqualTo(22));
            Assert.That(groups[0].PatternKey, Is.Null);
            Assert.That(summariser.TotalDeltas, Is.EqualTo(22));
        }
    }

    [Test]
    public void BuildGroups_CollapsedGroupOfMixedPatterns_StillLabelsEachKeptDeltaIndividually()
    {
        var summariser = new PreviewSummariser(maximumDeltasPerGroup: null, maximumValuePairsPerGroup: 1);
        summariser.Add(Delta("bob@contoso.com", "bob@fabrikam.com"));
        summariser.Add(Delta("bsmith", "svc-bsmith"));
        summariser.Add(Delta("Sales", "Marketing"));

        var deltas = summariser.BuildGroups(Guid.CreateVersion7(), NoConnectedSystems).SelectMany(g => g.Deltas).ToList();

        Assert.That(deltas, Has.Count.EqualTo(3));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(deltas.Count(d => d.PatternKey == PreviewPatternKeys.EmailDomainChanged), Is.EqualTo(1));
            Assert.That(deltas.Count(d => d.PatternKey == PreviewPatternKeys.PrefixAdded), Is.EqualTo(1));
            Assert.That(deltas.Count(d => d.PatternKey == null), Is.EqualTo(1),
                "the group cannot be labelled, but the rows behind it still can be, and that is where the drill-down looks");
        }
    }

    [Test]
    public void BuildGroups_DeltasWithNoRecognisablePattern_LeaveTheKeyUnset()
    {
        var summariser = new PreviewSummariser(maximumDeltasPerGroup: null);
        summariser.Add(Delta("2026-09-01", "2026-10-15"));

        var groups = summariser.BuildGroups(Guid.CreateVersion7(), NoConnectedSystems);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(groups[0].PatternKey, Is.Null);
            Assert.That(groups[0].Deltas.First().PatternKey, Is.Null);
        }
    }

    #endregion
}
