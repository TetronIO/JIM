// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.LDAP;

/// <summary>
/// What <see cref="LdapImportValueReader"/> made of one attribute's values: those it read as the attribute's type, and
/// those it did not import, each exactly as the directory returned it (#2128).
/// </summary>
internal sealed class LdapImportValues<T>
{
    // Allocated only for the rare attribute with a value that is not imported.
    private List<LdapUnreadableValue>? _unreadable;

    /// <summary>
    /// The values read as the attribute's type, without duplicates.
    /// </summary>
    internal List<T> Values { get; } = [];

    /// <summary>
    /// Each value that was not imported, as received: one that could not be read as the attribute's type, or one of
    /// two dates that differ only below the 100 nanoseconds JIM can read (#2153).
    /// </summary>
    internal IReadOnlyList<LdapUnreadableValue> Unreadable => _unreadable ?? [];

    /// <summary>
    /// Keeps a value that is not imported, to report it.
    /// </summary>
    /// <param name="value">The value as received.</param>
    /// <param name="reason">Why, where the attribute's type does not say; null for a value that did not read as the type.</param>
    internal void AddUnreadable(string value, string? reason = null) => (_unreadable ??= []).Add(new LdapUnreadableValue(value, reason));
}
