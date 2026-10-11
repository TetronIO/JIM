// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Logic;

/// <summary>
/// Where a value built from a generated value is written unchanged (Generated Value Forms, #2023): a Connected System and
/// its attribute, with their names for display.
/// </summary>
public sealed class GeneratedValueBuiltValueTarget
{
    /// <summary>
    /// The Connected System the value is written to.
    /// </summary>
    public int ConnectedSystemId { get; init; }

    /// <summary>
    /// The Connected System's name.
    /// </summary>
    public string ConnectedSystemName { get; init; } = string.Empty;

    /// <summary>
    /// The Connected System attribute the value is written to.
    /// </summary>
    public int AttributeId { get; init; }

    /// <summary>
    /// The Connected System attribute's name.
    /// </summary>
    public string AttributeName { get; init; } = string.Empty;
}
