// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;

namespace JIM.Scim.Schema;

/// <summary>
/// The outcome of reading one SCIM resource: the attribute values JIM will stage, and anything about
/// the resource that an administrator needs to know.
/// </summary>
public class ScimResourceReadResult
{
    public List<ConnectedSystemImportObjectAttribute> Attributes { get; init; } = [];

    /// <summary>
    /// Non-fatal problems with this resource, for example two entries sharing a canonical type where
    /// only one flattened slot exists. Reported per object rather than dropped, so a partial value is
    /// never presented as a complete one.
    /// </summary>
    public List<string> Warnings { get; init; } = [];

    /// <summary>
    /// Every value the resource held that could not be converted to its attribute's type, for example a decimal
    /// outside the range JIM can hold (#2066). The attribute is left out of <see cref="Attributes"/>, and the import
    /// reports each value so JIM can leave the attribute as it already held it rather than read it as empty.
    /// </summary>
    public List<ConnectedSystemImportObjectAttributeValueError> UnreadableValues { get; init; } = [];

    /// <summary>
    /// A problem serious enough that the resource cannot be read at all, for example a resource that is not a JSON
    /// object. Null when the resource could be read; an unreadable value is reported in <see cref="UnreadableValues"/>.
    /// </summary>
    public string? Error { get; init; }
}
