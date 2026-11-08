// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// A multi-valued reference attribute a Delta Synchronisation's configuration keeps whole (#2818), and why.
/// </summary>
/// <param name="ObjectTypeId">The Object Type.</param>
/// <param name="AttributeId">The attribute.</param>
/// <param name="AttributeName">Its name.</param>
/// <param name="Reason">Why it is loaded whole.</param>
public sealed record SynchronisationValueLoadFallback(int ObjectTypeId, int AttributeId, string AttributeName, SynchronisationValueLoadFallbackReason Reason);
