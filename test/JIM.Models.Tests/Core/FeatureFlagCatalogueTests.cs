// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JIM.Models.Core;
using JIM.TestSupport;
using NUnit.Framework;

namespace JIM.Models.Tests.Core;

/// <summary>
/// Guards the shape of the feature-flag catalogue (#1781): every entry has a well-formed key, a non-empty display
/// name and description, and a real tracking issue number, and keys are unique. Nothing here pins ordinals (the
/// catalogue is not persisted by position; each flag is a Service Setting row keyed by its string <c>Key</c>),
/// but every declared flag must be reachable by that key.
/// </summary>
[TestFixture]
public class FeatureFlagCatalogueTests
{
    [Test]
    public void All_EveryDefinition_HasAWellFormedKey()
    {
        foreach (var definition in FeatureFlagCatalogue.All)
        {
            Assert.That(definition.Key, Does.StartWith("Features."),
                $"'{definition.Key}' should use the 'Features.<Name>' convention so a flag's Service Setting key is recognisable at a glance.");
        }
    }

    [Test]
    public void All_EveryDefinition_HasDisplayNameAndDescription()
    {
        foreach (var definition in FeatureFlagCatalogue.All)
        {
            Assert.That(definition.DisplayName, Is.Not.Null.And.Not.Empty, $"{definition.Key} has no display name");
            Assert.That(definition.Description, Is.Not.Null.And.Not.Empty, $"{definition.Key} has no description");
        }
    }

    [Test]
    public void All_EveryDefinition_HasATrackingIssueNumber()
    {
        foreach (var definition in FeatureFlagCatalogue.All)
        {
            Assert.That(definition.TrackingIssueNumber, Is.GreaterThan(0),
                $"{definition.Key} has no tracking issue for its own removal; file one when introducing a flag.");
        }
    }

    [Test]
    public void All_Keys_AreUnique()
    {
        var duplicates = FeatureFlagCatalogue.All
            .GroupBy(d => d.Key)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.That(duplicates, Is.Empty, "duplicate feature flag keys: " + string.Join(", ", duplicates));
    }

    [Test]
    public void Use_SubstitutesTheCatalogueUntilDisposed()
    {
        var declared = FeatureFlagCatalogue.All;

        using (FeatureFlagCatalogueScope.Use(FeatureFlagCatalogueScope.InDevelopmentFlag))
        {
            Assert.That(FeatureFlagCatalogue.All, Is.EqualTo(new[] { FeatureFlagCatalogueScope.InDevelopmentFlag }));
        }

        Assert.That(FeatureFlagCatalogue.All, Is.SameAs(declared), "disposing the scope must restore the declared catalogue");
    }

    [Test]
    public async Task Use_IsScopedToTheCallingFlowAsync()
    {
        using var scope = FeatureFlagCatalogueScope.Use(FeatureFlagCatalogueScope.PreviewFlag);

        // A flow started from a clean execution context (as a concurrently running test's would be) must not see this
        // test's substitute.
        Task<IReadOnlyList<FeatureFlagDefinition>> elsewhere;
        using (ExecutionContext.SuppressFlow())
            elsewhere = Task.Run(() => FeatureFlagCatalogue.All);
        var seenElsewhere = await elsewhere;

        Assert.That(seenElsewhere, Does.Not.Contain(FeatureFlagCatalogueScope.PreviewFlag));
        Assert.That(FeatureFlagCatalogue.All, Does.Contain(FeatureFlagCatalogueScope.PreviewFlag));
    }
}
