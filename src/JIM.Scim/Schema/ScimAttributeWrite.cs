// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Scim.Schema;

/// <summary>
/// One JIM attribute's values, on their way to a service provider.
/// </summary>
/// <param name="AttributeName">
/// The flattened Connected System Attribute name, for example <c>name.givenName</c> or <c>emails.work</c>.
/// </param>
/// <param name="Values">
/// The values as JIM holds them (string, bool, int, long, decimal, DateTime, Guid, byte[]). The writer
/// converts each to its SCIM JSON form from the attribute's declared type, so the caller does not need
/// to know how SCIM spells a date or a binary value.
/// </param>
public sealed record ScimAttributeWrite(string AttributeName, IReadOnlyList<object?> Values);
