// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Transactional;

namespace JIM.Web.Models.Api;

/// <summary>
/// A value the preview generated that the real synchronisation would also probe for (Unique Value Generation, #242,
/// release 3). The preview checks JIM's own records only; when the synchronisation runs, JIM also asks each of
/// <see cref="ConnectedSystemNames"/> whether the value is already in use, and generates a different value if one of
/// them already has an account using it.
/// </summary>
public class SyncPreviewGeneratedValueProbeDto
{
    /// <summary>
    /// The Metaverse attribute the value was generated for or, for a value built from a generated value (Generated Value
    /// Forms, #2023), the attribute that value is held in: Email, or the Connected System attribute an export expression
    /// writes.
    /// </summary>
    public string AttributeName { get; set; } = null!;

    /// <summary>
    /// For a value built from a generated value (Generated Value Forms, #2023), the generated attribute it is built from
    /// (Account Name): a collision on the value makes JIM generate a different one of those. Null for the generated value
    /// itself.
    /// </summary>
    public string? BuiltFromAttributeName { get; set; }

    /// <summary>The value the preview generated.</summary>
    public string Value { get; set; } = null!;

    /// <summary>The Connected Systems the synchronisation would probe for the value, by name.</summary>
    public List<string> ConnectedSystemNames { get; set; } = [];

    public static SyncPreviewGeneratedValueProbeDto FromModel(SyncPreviewGeneratedValueProbe model) => new()
    {
        AttributeName = model.AttributeName,
        BuiltFromAttributeName = model.BuiltFromAttributeName,
        Value = model.Value,
        ConnectedSystemNames = [.. model.ConnectedSystemNames]
    };
}
