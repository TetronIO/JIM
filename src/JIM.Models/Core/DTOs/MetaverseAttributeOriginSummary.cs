// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Core.DTOs;

/// <summary>
/// The distinct origins of one attribute's values on a Metaverse Object. A single-valued attribute has exactly one;
/// a multi-valued attribute usually has one, and has several only when its values came from different sources.
/// </summary>
public class MetaverseAttributeOriginSummary
{
    public int AttributeId { get; set; }

    public string AttributeName { get; set; } = null!;

    /// <summary>Distinct origins, most values first. Never empty.</summary>
    public List<ValueOrigin> Origins { get; set; } = new();

    public bool HasSeveralOrigins => Origins.Count > 1;
}
