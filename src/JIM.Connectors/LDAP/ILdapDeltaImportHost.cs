// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using System.DirectoryServices.Protocols;
namespace JIM.Connectors.LDAP;

/// <summary>
/// What a change source needs from the import that is running it: the conversions and reporting that belong to
/// the import as a whole (scope, exclusions, attribute conversion, progress) rather than to any one directory
/// type. Implemented by <c>LdapConnectorImport</c>; faked in tests.
/// </summary>
internal interface ILdapDeltaImportHost
{
    /// <summary>
    /// Converts directory entries into import objects, applying the import's scope and exclusion rules and
    /// resolving each entry's Object Type (from <paramref name="searchedObjectType"/> when the search was for one
    /// type, otherwise from the entry's objectClass).
    /// </summary>
    IEnumerable<ConnectedSystemImportObject> ConvertEntries(SearchResultEntryCollection entries, ObjectChangeType changeType, ConnectedSystemObjectType? searchedObjectType);

    /// <summary>
    /// Reads an object's current state by DN, for sources whose log names what changed but not how it now looks.
    /// Null when the object is no longer there.
    /// </summary>
    ConnectedSystemImportObject? GetObjectByDn(string dn, ObjectChangeType changeType);

    /// <summary>Moves the run's live progress to a named phase (see <c>LdapConnectorPhases</c>).</summary>
    Task EnterPhaseAsync(string phase, string message);

    /// <summary>Adds to the run's count of objects read so far, so the Activity's counters move as the read proceeds.</summary>
    Task ReportObjectsReadAsync(int objectsJustRead);
}
