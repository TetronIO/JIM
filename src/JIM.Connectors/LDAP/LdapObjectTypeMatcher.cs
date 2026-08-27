// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;

namespace JIM.Connectors.LDAP;

/// <summary>
/// Decides which Object Type a directory entry imports as, from the object classes it carries.
/// </summary>
/// <remarks>
/// An entry states its classes in no defined order, and once auxiliary classes can be selected as Object Types in
/// their own right an entry can match several of them. Both questions this answers, what an entry is and which
/// search emits it, therefore have to be settled by what the classes are rather than by where they appear.
/// </remarks>
internal static class LdapObjectTypeMatcher
{
    /// <summary>
    /// The Object Type an entry carrying these object classes imports as, or null when none of them is a selected
    /// Object Type.
    /// </summary>
    internal static ConnectedSystemObjectType? Match(
        IEnumerable<string> objectClasses,
        IEnumerable<ConnectedSystemObjectType> objectTypes)
    {
        var selected = objectTypes.Where(objectType => objectType.Selected).ToList();

        var candidates = objectClasses
            .Select(objectClass => selected.FirstOrDefault(objectType =>
                objectType.Name.Equals(objectClass, StringComparison.OrdinalIgnoreCase)))
            .OfType<ConnectedSystemObjectType>()
            .ToList();

        // A structural class is what an entry is; an auxiliary class is only something it also carries. Settling
        // that by class kind rather than by position is what makes an entry resolve to the same Object Type
        // whatever order the directory lists its classes in, which RFC 4512 leaves entirely open.
        //
        // Among structural classes the first still wins: Active Directory returns them most specific first, and
        // that ordering is the only statement of specificity JIM holds.
        return candidates.FirstOrDefault(candidate => !candidate.IsAuxiliary()) ?? candidates.FirstOrDefault();
    }

    /// <summary>
    /// Whether the search for <paramref name="searchedObjectType"/> is the one that emits this entry, or whether it
    /// should leave the entry to the search for the Object Type it actually resolved to.
    /// </summary>
    /// <remarks>
    /// An import runs one search per selected Object Type, filtered on that type's class, so an entry carrying two
    /// selected classes comes back from two searches. Emitting it from both would stage one directory entry as two
    /// Connected System Objects. The search for the type it resolves to always returns it, because the entry
    /// carries that class by definition, so deferring to that one loses nothing.
    /// <para>
    /// A caller with no Object Type in hand, i.e. fetching a single object by its DN, has no other search to defer
    /// to and always emits.
    /// </para>
    /// </remarks>
    internal static bool OwnsEntry(ConnectedSystemObjectType matched, ConnectedSystemObjectType? searchedObjectType)
    {
        return searchedObjectType == null ||
               matched.Name.Equals(searchedObjectType.Name, StringComparison.OrdinalIgnoreCase);
    }
}
