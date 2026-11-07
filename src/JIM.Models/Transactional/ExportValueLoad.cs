// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;

namespace JIM.Models.Transactional;

/// <summary>
/// How export execution loads one object's values (#2816), as <see cref="ExportValueLoadPlanner.BuildValueLoads"/> decides
/// it: the attributes it loads in part, and those large enough to have been that it loads whole. The load records both on the
/// object (<see cref="ConnectedSystemObject.PartiallyLoadedAttributes"/>, <see cref="ConnectedSystemObject.LoadedWholeAttributes"/>).
/// </summary>
public sealed class ExportValueLoad
{
    /// <summary>
    /// The attributes loaded in part, by attribute id.
    /// </summary>
    public required IReadOnlyDictionary<int, ExportPartiallyLoadedAttribute> PartiallyLoadedAttributes { get; init; }

    /// <summary>
    /// The attributes large enough to load in part that are loaded whole, by attribute id, with the reason.
    /// </summary>
    public required IReadOnlyDictionary<int, ExportValueLoadFallbackReason> LoadedWholeAttributes { get; init; }

    /// <summary>
    /// Records the load on the object it describes.
    /// </summary>
    public void ApplyTo(ConnectedSystemObject connectedSystemObject)
    {
        connectedSystemObject.PartiallyLoadedAttributes = PartiallyLoadedAttributes.Count > 0
            ? PartiallyLoadedAttributes.ToDictionary(pair => pair.Key, pair => (PartiallyLoadedAttribute)pair.Value)
            : null;
        connectedSystemObject.LoadedWholeAttributes = LoadedWholeAttributes.Count > 0 ? LoadedWholeAttributes : null;
    }
}
