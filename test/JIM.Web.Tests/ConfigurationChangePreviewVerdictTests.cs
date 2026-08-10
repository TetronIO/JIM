// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using JIM.Models.Activities;
using JIM.Models.Preview;
using JIM.Web.Causality;
using JIM.Web.Models;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The sentence a Configuration Change Preview leads with (#1275). Its whole job is to put the worst consequence
/// first, so the ordering rule is the thing under test: a change that disconnects tens of thousands of objects and
/// deletes two must still lead with the two deletions, which count-ordering would bury.
/// </summary>
[TestFixture]
public class ConfigurationChangePreviewVerdictTests
{
    [Test]
    public void Describe_NoCounts_SaysNothing()
    {
        // The summary's "nothing would change" alert already states this; a verdict beside it would be a second,
        // weaker statement of the same thing.
        Assert.That(ConfigurationChangePreviewVerdict.Describe([]), Is.Null);
    }

    [Test]
    public void Describe_EveryCountIsZero_SaysNothing()
    {
        var counts = new[] { Count(ActivityRunProfileExecutionItemSyncOutcomeType.WouldFallOutOfScope, 0) };

        Assert.That(ConfigurationChangePreviewVerdict.Describe(counts), Is.Null);
    }

    [Test]
    public void Describe_SeverestTransitionHasTheSmallestCount_StillLeadsWithIt()
    {
        var counts = new[]
        {
            Count(ActivityRunProfileExecutionItemSyncOutcomeType.WouldFallOutOfScope, 40_000),
            Count(ActivityRunProfileExecutionItemSyncOutcomeType.WouldBecomeDeletionEligible, 2)
        };

        var verdict = ConfigurationChangePreviewVerdict.Describe(counts);

        Assert.That(verdict, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(verdict!.Lead, Is.EqualTo("2 objects would become eligible for deletion."));
            Assert.That(verdict.Detail, Is.EqualTo("40,000 objects would leave import scope."));
            Assert.That(verdict.Severity, Is.EqualTo(Severity.Error));
        }
    }

    [Test]
    public void Describe_OnlyRecoverableTransitions_TakesTheirSeverity()
    {
        var counts = new[] { Count(ActivityRunProfileExecutionItemSyncOutcomeType.WouldFallOutOfScope, 3) };

        var verdict = ConfigurationChangePreviewVerdict.Describe(counts);

        Assert.That(verdict, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(verdict!.Severity, Is.EqualTo(Severity.Warning));
            Assert.That(verdict.Detail, Is.Null, "A single transition has nothing to put in the supporting sentence");
        }
    }

    [Test]
    public void Describe_AnImprovement_ReadsAsOne()
    {
        // Widening scope back out is the reassuring direction, and a red alert stating it would be wrong.
        var counts = new[] { Count(ActivityRunProfileExecutionItemSyncOutcomeType.WouldCeaseToBeDeletionEligible, 5) };

        var verdict = ConfigurationChangePreviewVerdict.Describe(counts);

        Assert.That(verdict, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(verdict!.Severity, Is.EqualTo(Severity.Success));
            Assert.That(verdict.Lead, Is.EqualTo("5 objects would no longer be eligible for deletion."));
        }
    }

    [Test]
    public void Describe_ASingleObject_IsNotWrittenAsAPlural()
    {
        var counts = new[] { Count(ActivityRunProfileExecutionItemSyncOutcomeType.WouldBecomeDeletionEligible, 1) };

        Assert.That(ConfigurationChangePreviewVerdict.Describe(counts)!.Lead,
            Is.EqualTo("1 object would become eligible for deletion."));
    }

    [Test]
    public void Describe_LargeCounts_AreGrouped()
    {
        var counts = new[] { Count(ActivityRunProfileExecutionItemSyncOutcomeType.WouldFallOutOfScope, 1_234_567) };

        Assert.That(ConfigurationChangePreviewVerdict.Describe(counts)!.Lead,
            Does.Contain("1,234,567"));
    }

    [Test]
    public void EveryPreviewTransition_HasASentenceForm()
    {
        // Without one the verdict falls back to the plain label, which reads as a fragment mid-sentence. The
        // fallback exists so a new transition renders rather than throws; this test is what stops it being used.
        var previewTransitions = Enum.GetValues<ActivityRunProfileExecutionItemSyncOutcomeType>()
            .Where(t => t.ToString().StartsWith("Would", StringComparison.Ordinal));

        foreach (var transition in previewTransitions)
        {
            Assert.That(OutcomeDisplayMap.Get(transition).SentenceForm, Is.Not.Null.And.Not.Empty,
                $"{transition} has no sentence form, so the preview's leading sentence cannot state it");
        }
    }

    private static PreviewImpactCount Count(ActivityRunProfileExecutionItemSyncOutcomeType transition, int objectCount) =>
        new(transition, objectCount, null, null);
}
