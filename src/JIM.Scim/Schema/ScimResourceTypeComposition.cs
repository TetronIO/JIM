// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Scim.Schema;

/// <summary>
/// The attributes each resource type is composed of, as <see cref="ScimResourceTypeComposer"/> derives them.
/// </summary>
public class ScimResourceTypeComposition
{
    /// <summary>
    /// The flattened attributes of each resource type, keyed by the resource type's name without regard to case.
    /// </summary>
    public required Dictionary<string, List<ScimFlattenedAttribute>> FlattenedAttributes { get; init; }

    /// <summary>
    /// Gaps in what the provider published, worth telling an administrator about: a resource type with no schema, or
    /// an extension with no definition.
    /// </summary>
    public required List<string> Warnings { get; init; }
}
