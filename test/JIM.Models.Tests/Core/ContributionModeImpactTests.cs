// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using NUnit.Framework;

namespace JIM.Models.Tests.Core;

/// <summary>
/// Which of today's counts each change of contribution mode reports (#3059, Phase 3; PRD requirements 16 and 17).
/// </summary>
[TestFixture]
public class ContributionModeImpactTests
{
    // 1,200 objects hold values no contributor supplies (3,000 values); 500 hold values from several contributors, 200 of
    // them among the 1,200, so 1,500 objects lose values under Priority; 80 objects could gain values.
    private static readonly ContributionModeImpactCounts Counts = new(3_000, 1_200, 500, 1_500, 80);

    /// <summary>
    /// PRD Scenario 7: leaving Accumulate states how many objects will lose values, and must be confirmed.
    /// </summary>
    [Test]
    public void From_AccumulateToMerge_ReportsTheValuesNoLongerSuppliedAndRequiresConfirmation()
    {
        var impact = ContributionModeImpact.From(10, "Account Audit Log", AttributeContributionMode.Accumulate, AttributeContributionMode.Merge, Counts);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(impact.ObjectsLosingValues, Is.EqualTo(1_200));
            Assert.That(impact.ValuesNoLongerSupplied, Is.EqualTo(3_000));
            Assert.That(impact.ObjectsKeepingOneContributor, Is.Zero, "Merge keeps every contributor's values");
            Assert.That(impact.ObjectsGainingValues, Is.Zero);
            Assert.That(impact.RequiresConfirmation, Is.True);
        }
    }

    [Test]
    public void From_AccumulateToPriority_CountsBothKindsOfLossOnce()
    {
        var impact = ContributionModeImpact.From(10, "Account Audit Log", AttributeContributionMode.Accumulate, AttributeContributionMode.Priority, Counts);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(impact.ObjectsLosingValues, Is.EqualTo(1_500));
            Assert.That(impact.ValuesNoLongerSupplied, Is.EqualTo(3_000));
            Assert.That(impact.ObjectsKeepingOneContributor, Is.EqualTo(500));
            Assert.That(impact.RequiresConfirmation, Is.True);
        }
    }

    [Test]
    public void From_MergeToPriority_ReportsObjectsNarrowedToOneContributorWithoutConfirmation()
    {
        var impact = ContributionModeImpact.From(10, "Email Addresses", AttributeContributionMode.Merge, AttributeContributionMode.Priority, Counts);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(impact.ObjectsKeepingOneContributor, Is.EqualTo(500));
            Assert.That(impact.ValuesNoLongerSupplied, Is.Zero, "Merge holds no value its contributors do not supply");
            Assert.That(impact.RequiresConfirmation, Is.False);
        }
    }

    [TestCase(AttributeContributionMode.Accumulate)]
    [TestCase(AttributeContributionMode.Merge)]
    public void From_PriorityToAMultiContributorMode_ReportsObjectsGainingValuesOnly(AttributeContributionMode target)
    {
        var impact = ContributionModeImpact.From(10, "Email Addresses", AttributeContributionMode.Priority, target, Counts);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(impact.ObjectsGainingValues, Is.EqualTo(80));
            Assert.That(impact.ObjectsLosingValues, Is.Zero);
            Assert.That(impact.RequiresConfirmation, Is.False);
        }
    }

    [Test]
    public void From_MergeToAccumulate_ChangesNothing()
    {
        var impact = ContributionModeImpact.From(10, "Email Addresses", AttributeContributionMode.Merge, AttributeContributionMode.Accumulate, Counts);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(impact.ObjectsLosingValues + impact.ObjectsGainingValues + impact.ValuesNoLongerSupplied, Is.Zero);
            Assert.That(impact.RequiresConfirmation, Is.False);
        }
    }
}
