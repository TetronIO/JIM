// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Application.UniqueValues;

/// <summary>
/// One Connected System attribute a generated value is probed against (Unique Value Generation, #242, release 3):
/// an attribute the value flows to directly, in a Connected System whose Connector may probe.
/// </summary>
/// <param name="ConnectedSystemId">The Connected System to search.</param>
/// <param name="ConnectedSystemObjectTypeAttributeId">The attribute to search for the candidates.</param>
public sealed record UniquenessProbeTarget(int ConnectedSystemId, int ConnectedSystemObjectTypeAttributeId);
