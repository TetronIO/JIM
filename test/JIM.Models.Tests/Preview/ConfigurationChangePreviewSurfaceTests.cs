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
    public void ToActivityTargetType_NoTwoSurfaces_ShareATargetType()
    {
        // Two surfaces mapping to one target type would make the change history ambiguous about which preview
        // produced a result, and would let a registry lookup by target type return the wrong adapter.
        var duplicates = PreviewableSurfaces()
            .GroupBy(ConfigurationChangePreviewSurfaces.ToActivityTargetType)
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key}: {string.Join(" and ", group)}")
            .ToList();

        Assert.That(duplicates, Is.Empty, "preview surfaces sharing an Activity target type: " + string.Join("; ", duplicates));
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
