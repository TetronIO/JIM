// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.LDAP;

/// <summary>
/// What <see cref="LdapImportValueReader"/> made of one attribute's values: those it read as the attribute's type, and
/// those it could not, each exactly as the directory returned it (#2128).
/// </summary>
internal sealed class LdapImportValues<T>
{
    // Allocated only for the rare attribute with a value that cannot be read.
    private List<string>? _unreadable;

    /// <summary>
    /// The values read as the attribute's type, without duplicates.
    /// </summary>
    internal List<T> Values { get; } = [];

    /// <summary>
    /// Each value that could not be read, as received: the text the directory returned, or hexadecimal for a binary
    /// value.
    /// </summary>
    internal IReadOnlyList<string> Unreadable => _unreadable ?? [];

    internal void AddUnreadable(string value) => (_unreadable ??= []).Add(value);
}
