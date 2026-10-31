// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.ComponentModel.DataAnnotations.Schema;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Utility;
namespace JIM.Models.Transactional;

public class PendingExport
{
    // this object will get created/updated when:
    // - a synchronisation is performed on a Connected System Object not in the Connected System this Pending Export
    //   is for, and where that sync results in changes to this Connected System.
    // - a Metaverse Object change is committed to an object along the same lines. If the change would result in this
    //   Connected System being updated, then a Pending Export object is needed (essentially, an MVO change results
    //   in the same outcome, a change to this Connected System).

    // the Pending Export is processed on an export sync run, with the change being attempted against
    // the Connected System. Changes would be made and then when a confirming import and sync is performed, the sync
    // would work out if the Pending Export was fully applied. If only partially applied, then the Pending Export
    // will have the relevant committed changes removed and any error count necessary, increased.

    // this allows the admin to see what exports are failing, whether it's just once, or multiple times.

    // expected results for export operations:
    // - create object: success or fail
    // - delete object: success or fail
    // - update attribute: atomic, some can succeed, some can fail

    public Guid Id { get; set; }

    /// <summary>
    /// If the change type is create, then it's essential we know what Connected System this applies to :)
    /// </summary>
    public ConnectedSystem ConnectedSystem { get; set; } = null!;
    public int ConnectedSystemId { get; set; }

    /// <summary>
    /// If the change type is delete or update, then we can link an existing Connected System Object.
    /// If the change type is create, then there won't be a Connected System Object yet that we can link.
    /// </summary>
    public ConnectedSystemObject? ConnectedSystemObject { get; set; }

    /// <summary>
    /// Foreign key for ConnectedSystemObject. Explicit FK ensures EF Core persists the relationship correctly.
    /// </summary>
    public Guid? ConnectedSystemObjectId { get; set; }

    public PendingExportChangeType ChangeType { get; set; }

    // Not a List: a group's values can number hundreds of thousands, and EF Core loads a List in time that grows with
    // the square of its size. See ReferenceIndexedList.
    public ReferenceIndexedList<PendingExportAttributeValueChange> AttributeValueChanges { get; set; } = new();

    public PendingExportStatus Status { get; set; } = PendingExportStatus.Pending;

    #region Failure Tracking

    // JIM never gives up on a Pending Export (#2119): one whose attempt failed stays queued and is attempted again at
    // every export run, and is reported on every run, until it is written or no longer needed. These fields describe
    // the current run of failed attempts, for reporting only; none of them decides whether the export is attempted.

    /// <summary>
    /// How many attempts in a row have failed. Reset to zero when an attempt succeeds.
    /// </summary>
    public int ErrorCount { get; set; }

    /// <summary>
    /// When the first of the current run of failed attempts was made, or null when the last attempt did not fail.
    /// Shows an administrator how long an export has been failing.
    /// </summary>
    public DateTime? FailingSince { get; set; }

    /// <summary>
    /// When was the last export attempt made?
    /// </summary>
    public DateTime? LastAttemptedAt { get; set; }

    /// <summary>
    /// The error message from the last failed attempt, or null when the last attempt did not fail.
    /// </summary>
    public string? LastErrorMessage { get; set; }

    /// <summary>
    /// The stack trace from the last failed attempt, if available.
    /// </summary>
    public string? LastErrorStackTrace { get; set; }

    /// <summary>
    /// Clears the failure record once an attempt has written the export, so it no longer reads as failing.
    /// </summary>
    public void ClearFailure()
    {
        ErrorCount = 0;
        FailingSince = null;
        LastErrorMessage = null;
        LastErrorStackTrace = null;
    }

    #endregion

    #region MVO Tracking (Q1 Decision)

    /// <summary>
    /// The Metaverse Object that triggered this export.
    /// Used for tracking and reference resolution.
    /// </summary>
    public MetaverseObject? SourceMetaverseObject { get; set; }
    public Guid? SourceMetaverseObjectId { get; set; }

    /// <summary>
    /// Indicates whether this export has reference attributes that couldn't be resolved
    /// because the target objects don't yet exist in the target Connected System.
    /// </summary>
    public bool HasUnresolvedReferences { get; set; }

    #endregion

    /// <summary>
    /// The Synchronisation Rule whose provisioning decision produced this export, recorded only for a
    /// <see cref="PendingExportChangeType.Create"/>.
    /// <para>
    /// Recorded here because it is knowable exactly once, at the moment the provisioning decision is made, and
    /// is needed much later, when the account exists and can be given its first password. Working it out again
    /// at export time would mean re-evaluating scope against rules that may have been edited in the meantime,
    /// which is both expensive and capable of reaching a different answer than the one that created the account.
    /// </para>
    /// <para>
    /// Null for updates and deletes, for exports staged before this was recorded, and for a create whose rule
    /// has since been deleted. A null therefore means "no initial password to deliver" rather than "look it up
    /// another way".
    /// </para>
    /// </summary>
    public SyncRule? ProvisioningSyncRule { get; set; }

    public int? ProvisioningSyncRuleId { get; set; }

    /// <summary>
    /// Whether a password follows this export, so a Connector whose target refuses to enable an account with no password
    /// (Active Directory) can hold the enable back, to be completed once the password is set. Set just before the export
    /// is sent:
    /// <list type="bullet">
    /// <item>on a <see cref="PendingExportChangeType.Create"/> whose <see cref="ProvisioningSyncRuleId"/> names a
    /// Synchronisation Rule whose Initial Password is enabled, so an Initial Password will be staged for the account once
    /// it exists (#2039); the LDAP Connector creates such an account disabled;</item>
    /// <item>on a <see cref="PendingExportChangeType.Update"/> to an account whose person is still owed a password on
    /// this Connected System, of any origin, queued, retrying or parked (#2158); the LDAP Connector sends such an Update
    /// disabled only if Active Directory refuses to enable the account for want of a password.</item>
    /// </list>
    /// Whatever arrives first is the account's first password, which is why the name speaks of an initial one. Worked
    /// out once per export batch by the export run; the Connector never sees the rule or any password material.
    /// <para>
    /// Transient: never persisted, and false on every export the run has not decided for, so whether a password follows
    /// is read when the export is sent rather than stamped when the change was staged.
    /// </para>
    /// </summary>
    [NotMapped]
    public bool InitialPasswordFollows { get; set; }

    /// <summary>
    /// The Run Profile Execution Item of the synchronisation that staged this export, so the export run that
    /// later carries it out can say what caused it (#1223).
    /// <para>
    /// Recorded here for the same reason as <see cref="ProvisioningSyncRuleId"/> above: it is knowable exactly
    /// once, when the change is staged, and is needed much later by a different run. The export run has only a
    /// queue of changes to make; the decision that put this one in the queue was taken in another Activity, and
    /// no other column links the two. Its own Run Profile Execution Item cannot supply it either;
    /// <see cref="ActivityRunProfileExecutionItem.PendingExportId"/> is populated only on a
    /// <see cref="Enums.ObjectChangeType.PendingExport"/>-type item and is null on every ordinary
    /// <see cref="Enums.ObjectChangeType.Exported"/> one.
    /// </para>
    /// <para>
    /// Not a foreign key, matching the cause side of <see cref="CausalEdge"/>: the causing Activity ages out of
    /// history long before the effects it explains, and a cascade would delete the very link that answers "why
    /// did this export happen". Null for an export staged before this was recorded, and for the paths that
    /// stage one with no execution item in hand.
    /// </para>
    /// <para>
    /// The latest of <see cref="StagedByRunProfileExecutionItemIds"/>. Set only through <see cref="RecordStager"/>,
    /// so the two cannot disagree.
    /// </para>
    /// </summary>
    public Guid? QueuedByRunProfileExecutionItemId { get; private set; }

    /// <summary>
    /// The most runs a Pending Export records in <see cref="StagedByRunProfileExecutionItemIds"/>. An export that
    /// cannot be sent can sit queued for weeks, gaining a stager from every run that changes its object, so the
    /// list keeps the most recent rather than growing without bound.
    /// </summary>
    public const int MaxRecordedStagers = 50;

    /// <summary>
    /// Every synchronisation that staged a change into this export, oldest first (#2100).
    /// <para>
    /// One export can carry changes from several runs: a later run changing an object whose export is still queued
    /// merges its change into it rather than queueing a second export. Each of those runs caused the export, so each
    /// is recorded: the export run links back to every one of them, so each run's consequences reach the export and
    /// its confirmation, and the Causality Explorer's Queued by panel lists them. Not foreign keys, for the same
    /// reason as <see cref="QueuedByRunProfileExecutionItemId"/>.
    /// </para>
    /// <para>
    /// Empty for an export staged before this was recorded; <see cref="Stagers"/> then falls back to
    /// <see cref="QueuedByRunProfileExecutionItemId"/>. Recorded only through <see cref="RecordStager"/> and
    /// <see cref="InheritStagersFrom"/>.
    /// </para>
    /// </summary>
    public List<Guid> StagedByRunProfileExecutionItemIds { get; private set; } = [];

    /// <summary>
    /// Records a run as having staged a change into this export: it becomes the latest stager, and joins the list
    /// once, however many times it stages into the same export.
    /// </summary>
    /// <param name="runProfileExecutionItemId">The staging run's Run Profile Execution Item.</param>
    public void RecordStager(Guid runProfileExecutionItemId)
    {
        QueuedByRunProfileExecutionItemId = runProfileExecutionItemId;

        if (StagedByRunProfileExecutionItemIds.Contains(runProfileExecutionItemId))
            return;

        StagedByRunProfileExecutionItemIds.Add(runProfileExecutionItemId);
        var excess = StagedByRunProfileExecutionItemIds.Count - MaxRecordedStagers;
        if (excess > 0)
            StagedByRunProfileExecutionItemIds.RemoveRange(0, excess);
    }

    /// <summary>
    /// Carries the runs that staged an export this one replaces. A later run's change merges with an export still
    /// queued by deleting it and staging a replacement holding both, so the original's stagers are the
    /// replacement's too. Call before recording the replacing run, which then comes last.
    /// </summary>
    /// <param name="replaced">The export being replaced.</param>
    public void InheritStagersFrom(PendingExport replaced)
    {
        ArgumentNullException.ThrowIfNull(replaced);

        foreach (var stager in replaced.Stagers())
            RecordStager(stager);
    }

    /// <summary>
    /// The runs that staged this export, oldest first: the recorded list, or for an export staged before the list
    /// was recorded, its latest stager alone.
    /// </summary>
    public IReadOnlyList<Guid> Stagers()
    {
        if (StagedByRunProfileExecutionItemIds.Count > 0)
            return StagedByRunProfileExecutionItemIds;

        return QueuedByRunProfileExecutionItemId is { } latest ? [latest] : [];
    }

    /// <summary>
    /// When this Pending Export was created.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
