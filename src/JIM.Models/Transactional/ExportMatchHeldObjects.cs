// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional;

/// <summary>
/// The unjoined objects of a target type that hold an attribute-sourced Object Matching Rule's attribute only under
/// another data type (#2323): what export matching consults when the rule's lookup finds nothing.
/// </summary>
/// <remarks>
/// <para>
/// A schema refresh can change an attribute's data type while objects hold values of it, and those values stay where
/// the old type put them until an import reads the attribute again (#2128). The candidate lookup compares only the
/// column of the attribute's current type, so it cannot see such an object; and the old value is never compared
/// across types, since the type changed because the old reading was wrong. Any one of these objects may be the
/// identity's own account, so a lookup that finds nothing cannot be trusted while any are recorded: the rule cannot be
/// worked out, and provisioning would risk a duplicate.
/// </para>
/// <para>
/// Looked up once per run, on the first lookup that finds nothing, and kept for the run in
/// <see cref="ExportEvaluationCache.ExportMatchHeldObjects"/>: no import runs within a synchronisation, so the set can
/// only shrink as objects are joined, and an object joined since it was looked up stops later lookups in the run the
/// safe way, by failing them. What it cannot see is an object that became unjoined after it was looked up; the next
/// run's lookup considers it.
/// </para>
/// </remarks>
public sealed class ExportMatchHeldObjects
{
    /// <summary>How many objects hold the attribute only under another data type.</summary>
    public int Count { get; private set; }

    /// <summary>The first object recorded, and why its value cannot be read: what an error points the administrator at.</summary>
    public (Guid ConnectedSystemObjectId, string Reason)? First { get; private set; }

    /// <summary>Records an object holding the attribute only under another data type, and why its value cannot be read.</summary>
    public void Add(Guid connectedSystemObjectId, string reason)
    {
        Count++;
        First ??= (connectedSystemObjectId, reason);
    }
}
