// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.LDAP;

/// <summary>
/// A single attribute type and value pair within a Relative Distinguished Name,
/// for example the "CN" and "John Smith" in "CN=John Smith". The <see cref="Value"/>
/// is fully unescaped (RFC 4514 backslash and hex-pair escapes resolved, RFC 2253
/// quoting removed); the <see cref="Type"/> has surrounding whitespace trimmed.
/// </summary>
internal sealed record LdapAttributeTypeValue(string Type, string Value);
