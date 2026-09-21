// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.LDAP;

/// <summary>
/// The warnings a Delta Import gathers about changes it may not have seen, one per subject (a Deleted Objects
/// container, a changelog, an accesslog).
/// <para>
/// Keyed by subject so that later, stronger evidence about a subject replaces earlier, weaker evidence about the
/// same one: the up-front readiness check can only say it was unsure, whereas a search that is then refused, or
/// finds no container, knows for certain that nothing was detected there and why. A note about one subject never
/// displaces a note about another, so a run over several partitions reports each of them.
/// </para>
/// </summary>
internal sealed class LdapDeltaSourceNotes
{
    // Add-only use of Dictionary preserves insertion order, so the notes read in the order the subjects were
    // checked; a replacement keeps the original position.
    private readonly Dictionary<string, string> _bySubject = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Records what is known about one subject, replacing whatever was noted about it before.
    /// </summary>
    internal void Record(string subject, string note) => _bySubject[subject] = note;

    /// <summary>
    /// Every note, in one warning, or null when there is nothing to warn about. Null rather than empty because
    /// the connector chains this with its other import warnings by null-coalescing.
    /// </summary>
    internal string? Warning => _bySubject.Count > 0 ? string.Join(" ", _bySubject.Values) : null;
}
