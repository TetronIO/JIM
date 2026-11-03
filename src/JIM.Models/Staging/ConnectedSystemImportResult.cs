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
    /// JIM records it only once the run has staged every object it read. A run that fails or is cancelled before then leaves
    /// the previous value in place, so the next run is replayed the value the failed one started with and reads its
    /// changes again: expect to see a change more than once, never to miss one.
    /// </summary>
    public string? PersistedConnectorData { get; set; }

    /// <summary>
    /// Whether this import read every object in the Run Profile's scope, as a Full Import does, rather than only what has
    /// changed. Set it on every page of a Delta Import that reads everything instead of its changes: one that falls back
    /// to a full read because it has no watermark, or cannot continue from the one it has, and one configured to read
    /// everything. JIM then detects deletions by absence, exactly as it does on a Full Import: an object JIM holds that
    /// the run did not return is marked as deleted, within the Run Profile's deletion limits and the Partition it targets.
    /// </summary>
    /// <remarks>
    /// JIM treats every Full Import as a complete read whatever this says, so a Connector need not set it there; a
    /// Connector whose fallback runs its Full Import code may set it in that code, and so on both run types.
    ///
    /// JIM decides from the first page, before it processes a single object, because how a Delta Import treats what it
    /// reads depends on the answer, and every later page of a Delta Import must give the same answer: a page that
    /// contradicts the first fails the run before anything is saved. A Connector cannot change how it reads part-way
    /// through a run. A page returned once the run is being cancelled is not held to it.
    ///
    /// This covers every Partition the Run Profile reads. #2715 generalises it to the set of Partitions read completely,
    /// so that a run reading some Partitions in full and others by their changes detects deletions only in the former;
    /// a Connector setting this today is saying that set is every Partition the run targets.
    /// </remarks>
    public bool CompleteRead { get; set; }

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