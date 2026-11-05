// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Logic;

/// <summary>
/// A connection an export rule's scoping criteria test (#2021), as a scope test asks about it: whether the sample
/// Metaverse Object is joined to an object of the Connected System Object Type.
/// </summary>
/// <param name="ConnectedObjectTypeId">The Object Type the criteria name.</param>
/// <param name="Description">The type and its Connected System, for example "user in PANOPLY AD".</param>
public sealed record ConnectionSampleDescriptor(int ConnectedObjectTypeId, string Description);
