// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging.DTOs;

/// <summary>
/// One reference value a Connected System Object holds that has not been resolved to another Connected System Object,
/// as read from the connector space before any diagnosis (#959).
/// </summary>
public class ConnectedSystemObjectUnresolvedReferenceValue
{
    /// <summary>The attribute the value is on.</summary>
    public int AttributeId { get; set; }

    /// <summary>The attribute's name.</summary>
    public string AttributeName { get; set; } = null!;

    /// <summary>The reference value as imported.</summary>
    public string Value { get; set; } = null!;
}
