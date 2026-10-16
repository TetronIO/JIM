// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.LDAP;

/// <summary>
/// A value <see cref="LdapImportValueReader"/> did not import, and why where its attribute's data type does not say.
/// </summary>
/// <param name="Value">The value exactly as the directory returned it: the text, or hexadecimal for a binary value.</param>
/// <param name="Reason">Why the value was not imported, where that is not that it failed to read as its attribute's type,
/// such as one of several values of an attribute JIM holds one value for (#2153). Null for a value that did not read as
/// its type, whose reason the type gives (<see cref="UnreadableValueReasons.For"/>).</param>
internal readonly record struct LdapUnreadableValue(string Value, string? Reason = null);
