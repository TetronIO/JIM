// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using JIM.Models.Activities;
using JIM.Models.Preview;
using NUnit.Framework;

namespace JIM.Models.Tests.Preview;

/// <summary>
/// A preview run is an Activity, and an Activity records which configuration object it targeted in a per-type column
/// chosen by its <see cref="ActivityTargetType"/>. The preview surface therefore has to resolve to a target type, or
/// the preview's own Activity lands in the change history attached to nothing.
///
/// The two enums are deliberately separate: <see cref="ActivityTargetType"/> covers everything JIM records an
/// Activity for, including operational work no adapter could ever preview (a housekeeping sweep, a factory reset).
/// A registry keyed on it would accept nonsense. The cost of the separation is that the mapping can rot, which is
/// what these assert against.
///
/// The mapping is deliberately many-to-one: a surface is a kind of change rather than an entity (see the remarks on
/// <see cref="ConfigurationChangePreviewSurface"/>), so every surface must resolve to a target type but several may
/// resolve to the same one.
/// </summary>
[TestFixture]
public class ConfigurationChangePreviewSurfaceTests
{
    [Test]
    public void ToActivityTargetType_EverySurface_ResolvesToATargetType()
    {
        var unmapped = PreviewableSurfaces()
            .Where(surface => ConfigurationChangePreviewSurfaces.ToActivityTargetType(surface) == ActivityTargetType.NotSet)
            .ToList();

        Assert.That(unmapped, Is.Empty,
            "preview surface(s) with no Activity target type: " + string.Join(", ", unmapped) +
            ". A preview Activity for one of these would attach to no configuration object.");
    }

    [Test]
    public void ToActivityTargetType_SurfacesOnTheSameEntity_MayShareATargetType()
    {
        // A surface is a kind of change, not an entity, because exactly one adapter may serve a surface and one
        // entity's settings need several adapters: a Synchronisation Rule's Scoping Criteria, its Attribute Flow and
        // its deprovisioning actions are three different evaluations over three different populations. They all
        // target the same object, so they all map to the same target type, and that is the mapping working.
        //
        // This test previously asserted the opposite, on the reasoning that a shared target type would make the
        // change history ambiguous and could make a registry lookup by target type return the wrong adapter.
        // Neither holds: a preview Activity is identified by its own preview record, which carries the surface, and
        // no lookup is keyed on target type (the registry is keyed on the surface itself).
        var scopeTargetType = ConfigurationChangePreviewSurfaces.ToActivityTargetType(
            ConfigurationChangePreviewSurface.SynchronisationRuleScope);
        var deprovisioningTargetType = ConfigurationChangePreviewSurfaces.ToActivityTargetType(
            ConfigurationChangePreviewSurface.SynchronisationRule);

        Assert.That(scopeTargetType, Is.EqualTo(deprovisioningTargetType),
            "both surfaces preview changes to a Synchronisation Rule, so both Activities attach to one");
    }

    [Test]
    public void ToActivityTargetType_NotSet_Throws()
    {
        // NotSet is the uninitialised default, so it reaching the mapper means a caller forgot to set the surface.
        // Answering NotSet back would let that travel silently into an Activity nobody can find later.
        Assert.That(() => ConfigurationChangePreviewSurfaces.ToActivityTargetType(ConfigurationChangePreviewSurface.NotSet),
            Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    private static IEnumerable<ConfigurationChangePreviewSurface> PreviewableSurfaces() =>
        Enum.GetValues<ConfigurationChangePreviewSurface>().Where(s => s != ConfigurationChangePreviewSurface.NotSet);
}
