// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Web.Models;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Which lines the attribute editor's impact report shows for a change of contribution mode (#3059, Phase 3): one per
/// kind of effect the change has, and one saying nothing changes when it has none. Asserts on the counts the lines carry,
/// not their wording.
/// </summary>
[TestFixture]
public class ContributionModeImpactTextTests
{
    private static ContributionModeImpact Impact(int losing = 0, int noLongerSupplied = 0, int keepingOne = 0, int gaining = 0) => new()
    {
        ObjectsLosingValues = losing,
        ValuesNoLongerSupplied = noLongerSupplied,
        ObjectsKeepingOneContributor = keepingOne,
        ObjectsGainingValues = gaining
    };

    [Test]
    public void Lines_ObjectsLosingValues_StatesTheObjectCount()
    {
        var lines = ContributionModeImpactText.Lines(Impact(losing: 1_200, noLongerSupplied: 3_000));

        Assert.That(lines, Has.Count.EqualTo(1));
        Assert.That(lines[0], Does.Contain(1_200.ToString("N0")).And.Contain(3_000.ToString("N0")));
    }

    [Test]
    public void Lines_LosingAndNarrowing_StatesBoth()
    {
        var lines = ContributionModeImpactText.Lines(Impact(losing: 1_500, keepingOne: 500));

        Assert.That(lines, Has.Count.EqualTo(2));
    }

    [Test]
    public void Lines_ObjectsGainingValues_StatesTheObjectCount()
    {
        var lines = ContributionModeImpactText.Lines(Impact(gaining: 80));

        Assert.That(lines.Single(), Does.Contain("80"));
    }

    [Test]
    public void Lines_NoEffect_SaysNothingChanges()
    {
        var lines = ContributionModeImpactText.Lines(Impact());

        Assert.That(lines, Has.Count.EqualTo(1));
        Assert.That(lines[0], Does.Not.Match(@"\d"));
    }
}
