// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Scim.Schema;

/// <summary>
/// One change to one JIM attribute value, on its way into a SCIM PATCH request.
/// </summary>
/// <param name="AttributeName">
/// The flattened Connected System Attribute name, for example <c>emails.work</c> or <c>members</c>.
/// </param>
/// <param name="Operation">One of <see cref="JIM.Scim.Messages.ScimPatchOperations"/>.</param>
/// <param name="Value">
/// The value as JIM holds it. Still required on a removal from a multi-valued attribute, because the
/// path has to name which value is going.
/// </param>
public sealed record ScimAttributeChange(string AttributeName, string Operation, object? Value);
