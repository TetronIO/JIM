// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Models;

/// <summary>
/// A reference one Object Type holds to another in the SQL Object Types document, named so the editor can say
/// what removing the referenced Object Type takes with it.
/// </summary>
/// <param name="ObjectTypeName">The Object Type holding the reference.</param>
/// <param name="Name">The column, or the multi-valued attribute, that holds it.</param>
/// <param name="IsMultiValuedAttribute">Whether it is held by a multi-valued attribute's related table rather than a column.</param>
public sealed record SqlObjectTypeReference(string? ObjectTypeName, string? Name, bool IsMultiValuedAttribute);
