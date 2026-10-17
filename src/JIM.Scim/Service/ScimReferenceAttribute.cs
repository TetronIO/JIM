// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Scim.Service;

/// <summary>
/// A writable attribute whose values refer to other resources in the same store, held as reference rows rather than in
/// the resource's payload (D18): a Group's <c>members</c>, the Enterprise User <c>manager</c>, or a custom type's
/// equivalent. It is recognised by a <c>$ref</c> sub-attribute whose <c>referenceTypes</c> name resource types of the
/// store.
/// </summary>
/// <param name="Name">The name the store keys it by: unqualified for the base schema, URN-qualified for an extension's.</param>
/// <param name="SchemaUrn">The extension the attribute belongs to; null for the base schema.</param>
/// <param name="AttributeName">The attribute's name within its schema.</param>
/// <param name="MultiValued">Whether it holds many references (<c>members</c>) or one (<c>manager</c>).</param>
/// <param name="TargetTypeNames">The resource types a reference may point at (D9).</param>
public sealed record ScimReferenceAttribute(
    string Name,
    string? SchemaUrn,
    string AttributeName,
    bool MultiValued,
    IReadOnlyList<string> TargetTypeNames);
