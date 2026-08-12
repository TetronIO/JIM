// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;

namespace JIM.Models.Staging;

public class ConnectedSystemImportResult
{
    /// <summary>
    /// The objects imported from the Connected System, i.e. users, groups, etc.
    /// </summary>
    public List<ConnectedSystemImportObject> ImportObjects { get; set; } = new();

    /// <summary>
    /// Write any information to  this property that will be needed on the next import run to determine where to return results from.
    /// i.e. for an LDAP-based system this might store LDAP Cookies for numerous LDAP queries that have to be performed that would be passed in to the next query.
    /// This data is not persisted between synchronisation runs.
    /// Note: JIM will keep calling ImportAsync() until there are no more pagination tokens, as it understands this scenario to mean there is no more data to retrieve.
    /// </summary>
    public List<ConnectedSystemPaginationToken> PaginationTokens { get; set; } = new();

    /// <summary>
    /// Write any information to this property that you want to be made available on subsequent synchronisation runs.
    /// i.e. for an LDAP system you might write the last known change number here so that you can perform delta imports in the future.
    /// JIM will pass this data to Connectors on each synchronisation run.
    /// </summary>
    public string? PersistedConnectorData { get; set; }

    /// <summary>
    /// Optional warning message from the connector. When set, the import completes and the message is
    /// recorded on the Activity itself, deliberately not as an RPEI (a phantom RPEI with no CSO
    /// association would inflate the error counts). Use this to communicate non-fatal operational
    /// issues to the administrator (e.g., a delta import that fell back to a full import).
    /// </summary>
    public string? WarningMessage { get; set; }

    /// <summary>
    /// Optional error type classification for the warning. When <see cref="WarningMessage"/> is set,
    /// this categorises the warning for filtering and integration test assertions.
    /// </summary>
    public ActivityRunProfileExecutionItemErrorType? WarningErrorType { get; set; }

    /// <summary>
    /// How many entries this import call read from the Connected System and discarded because an excluded
    /// Container carved them out (#1255), one entry per excluded Container that discarded anything.
    /// </summary>
    /// <remarks>
    /// A Connector that cannot express "this subtree except that branch" in a single search has to read the
    /// excluded entries and throw them away, which is the deliberate choice made in #1255: decomposing the
    /// searches instead would make import scope depend on how recently the hierarchy was refreshed, and silently
    /// skip a Container created since. The design accepted the transfer cost on the condition that it is
    /// reported rather than hidden, and this is how a Connector reports it. Deliberately not a
    /// <see cref="WarningMessage"/>: an exclusion doing exactly what it was configured to do is not a warning,
    /// and flagging every exclusion-configured import as warned would train administrators to ignore the field.
    ///
    /// Empty is the ordinary case, including on a Connected System that carries exclusions but read nothing
    /// inside them. A Connector whose searches can honour an exclusion server-side leaves this empty and is
    /// reporting the truth: nothing was transferred to be discarded.
    /// </remarks>
    public List<ExclusionDiscardCount> EntriesDiscardedByExclusion { get; set; } = [];
}