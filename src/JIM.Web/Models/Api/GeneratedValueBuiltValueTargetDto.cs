// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Logic;

namespace JIM.Web.Models.Api;

/// <summary>
/// A Connected System attribute a value built from a generated value is written to unchanged (Generated Value Forms,
/// #2023).
/// </summary>
public class GeneratedValueBuiltValueTargetDto
{
    /// <summary>The Connected System.</summary>
    public int ConnectedSystemId { get; set; }

    /// <summary>The Connected System's name.</summary>
    public string ConnectedSystemName { get; set; } = null!;

    /// <summary>The Connected System attribute.</summary>
    public int AttributeId { get; set; }

    /// <summary>The Connected System attribute's name.</summary>
    public string AttributeName { get; set; } = null!;

    /// <summary>Creates the DTO from the read model.</summary>
    public static GeneratedValueBuiltValueTargetDto FromModel(GeneratedValueBuiltValueTarget model) => new()
    {
        ConnectedSystemId = model.ConnectedSystemId,
        ConnectedSystemName = model.ConnectedSystemName,
        AttributeId = model.AttributeId,
        AttributeName = model.AttributeName
    };
}
