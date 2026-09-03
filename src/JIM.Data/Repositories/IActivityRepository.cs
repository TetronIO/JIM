// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Enums;
using JIM.Models.Scheduling;
using JIM.Models.Transactional.DTOs;
using JIM.Models.Utility;

namespace JIM.Data.Repositories;

public interface IActivityRepository
{

    public Task CreateActivityAsync(Activity activity);

    /// <summary>
    /// Persists Run Profile Execution Items (including their sync outcome trees and any Connected System Object
    /// change snapshots carried on the outcomes) for an Activity that has already been persisted via
    /// <see cref="CreateActivityAsync"/> in the same unit of work. Items must reference related entities
    /// (Connected System Objects, Pending Exports) by scalar foreign key only; the implementation severs any
    /// navigation references to pre-existing entities so they cannot be re-inserted.
    /// Intended for small batches recorded outside sync task processing (for example Metaverse Object
    /// Housekeeping); sync processors use the bulk insert path on ISyncRepository instead.
    /// </summary>
    public Task CreateActivityRunProfileExecutionItemsAsync(IReadOnlyCollection<ActivityRunProfileExecutionItem> items);

    public Task UpdateActivityAsync(Activity activity);

    public Task DeleteActivityAsync(Activity activity);

    public Task<Activity?> GetActivityAsync(Guid id);

    /// <summary>
    /// Run Profile Safeguards (#1618): the newest Activity for an Export Run Profile execution against
    /// this Connected System that is no longer InProgress, so the Connected System page can show a
    /// notice when the most recent completed export withheld anything. Null when the system has no
    /// completed Export activity yet.
    /// </summary>
    public Task<Activity?> GetLatestCompletedExportActivityAsync(int connectedSystemId);

    /// <summary>
    /// Gets a page's worth of direct child activities for a given parent activity ID,
    /// ordered by creation date ascending.
    /// </summary>
    public Task<PagedResultSet<Activity>> GetChildActivitiesAsync(Guid parentActivityId, int page, int pageSize);

    /// <summary>
    /// Gets a window of one Activity's direct child Activities addressed by absolute <paramref name="offset"/>
    /// and <paramref name="count"/>, for the virtualised (infinite-scroll) child-Activity grid. Ordered by
    /// creation date ascending, and shares its query core with <see cref="GetChildActivitiesAsync"/> so the two
    /// reads can never disagree on which Activities are children.
    /// </summary>
    /// <param name="parentActivityId">The parent Activity whose direct children are wanted.</param>
    /// <param name="offset">The zero-based index of the first child wanted; negative values read as zero.</param>
    /// <param name="count">How many children are wanted; clamped to the repository's window-size cap.</param>
    /// <param name="includeTotalCount">Pass false to skip counting the whole match set when the caller already
    /// holds the total; the returned total is then null rather than zero
    /// (see <see cref="RangeResultSet{T}.TotalResults"/>).</param>
    public Task<RangeResultSet<Activity>> GetChildActivitiesRangeAsync(
        Guid parentActivityId,
        int offset,
        int count,
        string? searchQuery = null,
        bool includeTotalCount = true);

    /// <summary>
    /// One identity's most recent password changes and what each Connected System did with them (#1119,
    /// requirement 25), newest change first.
    /// <para>
    /// A purpose-built read rather than another set of filters on the general Activity list: that list already
    /// takes nineteen, none of them an identity, and this panel asks a single narrow question. It also needs the
    /// parents and their children together, which the general list cannot express at all.
    /// </para>
    /// <para>
    /// Read from Activities rather than from the queue because the queue row is deleted the moment the password
    /// arrives; the queue alone would show an identity's failures and none of its successes.
    /// </para>
    /// </summary>
    /// <param name="metaverseObjectId">The identity whose password changes are wanted.</param>
    /// <param name="maximumEvents">How many changes to return, newest first. The panel shows recent history, not
    /// an archive; the Activities list is where the whole record lives.</param>
    public Task<List<PasswordSynchronisationEvent>> GetPasswordSynchronisationEventsAsync(Guid metaverseObjectId, int maximumEvents);

    /// <summary>
    /// Returns a dictionary mapping each activity ID (from the provided set) to its direct child activity count.
    /// IDs with no children are omitted from the result.
    /// </summary>
    public Task<Dictionary<Guid, int>> GetChildActivityCountsAsync(IEnumerable<Guid> activityIds);

    /// <summary>
    /// Retrieves a page's worth of top-level Activities, i.e. those that do not have a parent Activity.
    /// Every filter is optional and they combine with AND; the multi-valued ones are additive/OR within
    /// themselves. Callers that want a subset of Activity kinds (Operations > History wants Worker Task
    /// Activities) express it through <paramref name="typeFilter"/> and <paramref name="operationFilter"/>.
    /// </summary>
    public Task<PagedResultSet<Activity>> GetActivitiesAsync(
        int page,
        int pageSize,
        string? searchQuery = null,
        string? sortBy = null,
        bool sortDescending = true,
        Guid? initiatedById = null,
        IEnumerable<ActivityTargetOperationType>? operationFilter = null,
        IEnumerable<ActivityOutcomeType>? outcomeFilter = null,
        IEnumerable<ActivityTargetType>? typeFilter = null,
        IEnumerable<ActivityStatus>? statusFilter = null,
        bool? hasChildActivities = null,
        IEnumerable<ActivityInitiatorType>? initiatorTypeFilter = null,
        DateTime? createdFrom = null,
        DateTime? createdTo = null,
        IEnumerable<string>? connectedSystemFilter = null,
        IEnumerable<string>? runProfileFilter = null,
        string? initiatedByFilter = null,
        bool? initiatedBySchedule = null,
        IEnumerable<Guid>? scheduleFilter = null);

    /// <summary>
    /// Retrieves a window of top-level Activities addressed by absolute <paramref name="startIndex"/> and
    /// <paramref name="count"/>, for virtualised (infinite-scroll) list views. Takes the same filters as
    /// <see cref="GetActivitiesAsync"/> and shares its query core, so the two reads can never disagree on
    /// which Activities match. Pass <paramref name="includeTotalCount"/> as false to skip counting the whole
    /// match set when the caller already holds the total; the returned total is then null rather than zero
    /// (see <see cref="RangeResultSet{T}.TotalResults"/>).
    /// </summary>
    public Task<RangeResultSet<Activity>> GetActivitiesRangeAsync(
        int startIndex,
        int count,
        string? searchQuery = null,
        string? sortBy = null,
        bool sortDescending = true,
        Guid? initiatedById = null,
        IEnumerable<ActivityTargetOperationType>? operationFilter = null,
        IEnumerable<ActivityOutcomeType>? outcomeFilter = null,
        IEnumerable<ActivityTargetType>? typeFilter = null,
        IEnumerable<ActivityStatus>? statusFilter = null,
        bool? hasChildActivities = null,
        IEnumerable<ActivityInitiatorType>? initiatorTypeFilter = null,
        DateTime? createdFrom = null,
        DateTime? createdTo = null,
        IEnumerable<string>? connectedSystemFilter = null,
        IEnumerable<string>? runProfileFilter = null,
        string? initiatedByFilter = null,
        bool? initiatedBySchedule = null,
        IEnumerable<Guid>? scheduleFilter = null,
        bool includeTotalCount = true);

    /// <summary>
    /// The distinct Connected Systems, Run Profiles and Schedules present in the Worker Task Activity
    /// history, for the Operations > History filter drop-downs.
    /// </summary>
    public Task<ActivityFilterOptions> GetWorkerTaskActivityFilterOptionsAsync();

    public Task<PagedResultSet<ActivityRunProfileExecutionItemHeader>> GetActivityRunProfileExecutionItemHeadersAsync(
        Guid activityId,
        int page,
        int pageSize,
        string? searchQuery = null,
        string? sortBy = null,
        bool sortDescending = false,
        IEnumerable<string>? objectTypeFilter = null,
        IEnumerable<ActivityRunProfileExecutionItemErrorType>? errorTypeFilter = null,
        IEnumerable<ActivityRunProfileExecutionItemSyncOutcomeType>? outcomeTypeFilter = null);

    /// <summary>
    /// Gets a window of one Activity's Run Profile Execution Item headers addressed by absolute
    /// <paramref name="offset"/> and <paramref name="count"/>, for the virtualised (infinite-scroll)
    /// execution-item grid. Takes the same search, sort and filters as
    /// <see cref="GetActivityRunProfileExecutionItemHeadersAsync"/> and shares its query core, so the two reads
    /// can never disagree on which items match.
    /// </summary>
    /// <param name="activityId">The Activity whose execution items are wanted.</param>
    /// <param name="offset">The zero-based index of the first item wanted; negative values read as zero.</param>
    /// <param name="count">How many items are wanted; clamped to the repository's window-size cap.</param>
    /// <param name="searchQuery">Optional case-insensitive search over the display name and external ID, live
    /// or snapshot.</param>
    /// <param name="sortBy">Optional sort key: "externalid", "displayname"/"name", "type"/"objecttype",
    /// "errortype", or the item's id (the default).</param>
    /// <param name="sortDescending">Whether the sort is descending.</param>
    /// <param name="objectTypeFilter">Optional filter for Connected System Object Type names (additive/OR
    /// within the filter).</param>
    /// <param name="errorTypeFilter">Optional filter for error types (additive/OR within the filter).</param>
    /// <param name="outcomeTypeFilter">Optional filter for sync outcome types (additive/OR within the
    /// filter).</param>
    /// <param name="includeTotalCount">Pass false to skip counting the whole match set when the caller already
    /// holds the total; the returned total is then null rather than zero
    /// (see <see cref="RangeResultSet{T}.TotalResults"/>).</param>
    public Task<RangeResultSet<ActivityRunProfileExecutionItemHeader>> GetActivityRunProfileExecutionItemHeadersRangeAsync(
        Guid activityId,
        int offset,
        int count,
        string? searchQuery = null,
        string? sortBy = null,
        bool sortDescending = false,
        IEnumerable<string>? objectTypeFilter = null,
        IEnumerable<ActivityRunProfileExecutionItemErrorType>? errorTypeFilter = null,
        IEnumerable<ActivityRunProfileExecutionItemSyncOutcomeType>? outcomeTypeFilter = null,
        bool includeTotalCount = true);

    public Task<ActivityRunProfileExecutionStats> GetActivityRunProfileExecutionStatsAsync(Guid activityId);

    /// <summary>
    /// Gets a lightweight progress snapshot for an Activity (#202): a scalar projection of the
    /// progress fields plus an operation-type breakdown from the Activity's stat counter rows.
    /// Cheap enough to serve at a high read frequency while a run is executing; never
    /// materialises Run Profile Execution Items. Returns null when the Activity does not exist.
    /// </summary>
    public Task<ActivityProgress?> GetActivityProgressAsync(Guid activityId);

    /// <summary>
    /// The recorded phases of a Run Profile execution (#454), in run order. Empty for Activities
    /// that are not Run Profile executions, and for runs that predate phase recording.
    /// </summary>
    public Task<List<ActivityPhase>> GetActivityPhasesAsync(Guid activityId);

    /// <summary>
    /// Finalises the Activity's Run Profile execution stat counters: recomputes the stats exactly
    /// from the persisted Run Profile Execution Items and Sync Outcomes, replaces the incremental
    /// counter rows with the exact values, and sets
    /// <see cref="Activity.RunProfileExecutionStatsFinalised"/> on the passed entity (the caller's
    /// subsequent <see cref="UpdateActivityAsync"/> persists the flag alongside the terminal
    /// status). Called by the completion paths so completed Activities serve stats from stored
    /// counters instead of re-aggregating; safe to call for Activities with no execution items.
    /// </summary>
    public Task FinaliseActivityRunProfileExecutionStatsAsync(Activity activity);

    public Task<ActivityRunProfileExecutionItem?> GetActivityRunProfileExecutionItemAsync(Guid id);

    /// <summary>
    /// Loads every causal edge whose effect is one of the given Run Profile Execution Items (#1223), so the
    /// upward walk can resolve a whole level of a cascade in one round trip.
    /// </summary>
    /// <remarks>
    /// Batched by design: a cohort can hold thousands of members, and a walk that queried per member would
    /// issue thousands of round trips to render one panel.
    /// </remarks>
    public Task<List<CausalEdge>> GetCausalEdgesByEffectRunProfileExecutionItemIdsAsync(IReadOnlyCollection<Guid> effectRunProfileExecutionItemIds);

    /// <summary>
    /// Maps Pending Export ids to the Run Profile Execution Item that executed each one's export (#1528), so
    /// the upward walk can follow a confirming import back to the export it confirms.
    /// </summary>
    /// <remarks>
    /// A confirming import's edge names the Pending Export it confirms and never an item: reconciliation
    /// deletes that Pending Export moments later, so the link has to be recorded there or not at all, and
    /// pairing a confirmation to an export by Connected System Object id alone can land on the wrong cycle
    /// because an object cycles through export and import repeatedly. The Pending Export id IS the cycle.
    /// The export execution's own <see cref="CausalEdgeType.PendingExportQueueingCausedExportExecution"/>
    /// edge carries the same id, so the pair identifies the executing item exactly. Nothing is written to
    /// support this: the join was always available, and only the walk was not spending it.
    /// </remarks>
    /// <param name="pendingExportIds">The Pending Export ids named by the confirmations at one level of the walk.</param>
    /// <returns>The executing item id per Pending Export id; ids with no recorded export execution are absent.</returns>
    public Task<Dictionary<Guid, Guid>> GetExportExecutionItemIdsByPendingExportIdsAsync(IReadOnlyCollection<Guid> pendingExportIds);

    /// <summary>
    /// Summarises the given Run Profile Execution Items for the causal walk (#1223): which still exist, and
    /// what each did to which Connected System Object.
    /// </summary>
    /// <remarks>
    /// Presence in the result is the retention check. The walk needs it to tell two situations apart that are
    /// otherwise identical: a cause with no edges above it is a genuine root and the chain is complete, whereas
    /// a cause whose item has aged out of history is a chain that was cut short. Both produce no further edges;
    /// only one of them lost information, and reporting the second as the first would tell an administrator
    /// they had the whole story when they did not.
    ///
    /// The change type, Connected System Object and Activity time carried per item are what let the walk
    /// continue past a synchronisation to the import that fed it, via
    /// <see cref="GetLatestImportItemForCsoAsync"/>.
    /// </remarks>
    public Task<Dictionary<Guid, CausalChainItemSummary>> GetRunProfileExecutionItemCausalSummariesAsync(
        IReadOnlyCollection<Guid> runProfileExecutionItemIds);

    /// <summary>
    /// The import event that last changed a Connected System Object at or before the given Activity time,
    /// excluding the asking item itself: the causal walk's source-import hop (#1223). Null where no import on
    /// the record is retained, in which case the chain ends at the synchronisation instead.
    /// </summary>
    public Task<CausalSourceImportEvent?> GetLatestImportItemForCsoAsync(
        Guid connectedSystemObjectId, DateTime atOrBeforeActivityExecuted, Guid excludeRunProfileExecutionItemId);

    /// <summary>
    /// The import event that last changed the record carrying the given external ID in the given Connected
    /// System, at or before the given Activity time, excluding the asking item itself: the source-import
    /// hop's degraded key (#1495). A deletion cascade hard-deletes the Connected System Object and nulls its
    /// id on every item that processed it, so the id-keyed walk of
    /// <see cref="GetLatestImportItemForCsoAsync"/> finds nothing exactly where an administrator most needs
    /// the chain; the external ID snapshotted on each item survives the deletion and reaches the same
    /// import. Null where no import on the record is retained.
    /// </summary>
    public Task<CausalSourceImportEvent?> GetLatestImportItemForExternalIdAsync(
        int connectedSystemId, string externalIdSnapshot, DateTime atOrBeforeActivityExecuted,
        Guid excludeRunProfileExecutionItemId);


    /// <summary>
    /// Gets all activities associated with a schedule execution.
    /// Used by the scheduler to determine step outcomes after worker tasks have been deleted.
    /// </summary>
    public Task<List<Activity>> GetActivitiesByScheduleExecutionAsync(Guid scheduleExecutionId);

    /// <summary>
    /// Gets all activities for a specific step within a schedule execution.
    /// A step may have multiple activities if it runs multiple Run Profiles in parallel.
    /// </summary>
    public Task<List<Activity>> GetActivitiesByScheduleExecutionStepAsync(Guid scheduleExecutionId, int stepIndex);

    /// <summary>
    /// What each Schedule Execution's tasks have left behind, keyed by execution (#1162): one
    /// observation per Activity, carrying the step it belongs to, its name and its status.
    /// </summary>
    /// <remarks>
    /// A projection rather than the Activities themselves, and batched across executions rather than
    /// queried per execution, because the Operations queue re-reads this on every progress
    /// notification. Callers that also hold the queue's Worker Tasks must discard the observations
    /// whose <see cref="ScheduleStepObservation.ActivityId"/> they already have, since a task that has
    /// started is described by both records at once.
    /// </remarks>
    public Task<Dictionary<Guid, List<ScheduleStepObservation>>> GetScheduleStepOutcomesAsync(IReadOnlyCollection<Guid> scheduleExecutionIds);

        /// <summary>
    /// Whether any Run Profile has ever been executed, in any state (in progress, complete, failed or cancelled).
    /// Backs the home page's "Run your first synchronisation" setup step, which asks only whether an administrator
    /// has run one, never how it turned out. Run Profile configuration changes (create, update, delete) carry the
    /// same <see cref="ActivityTargetType.ConnectedSystemRunProfile"/> target type as executions, so implementations
    /// must additionally require <see cref="ActivityTargetOperationType.Execute"/>.
    /// </summary>
    public Task<bool> HasAnyRunProfileExecutionAsync();

    /// <summary>
    /// Gets the highest configuration-change version recorded for a configuration object, identified by its activity
    /// target type (<see cref="ActivityTargetType.ConnectedSystem"/> or <see cref="ActivityTargetType.SynchronisationRule"/>) and
    /// database id, or 0 if none exist yet. Used to assign the next per-object version when capturing a configuration
    /// snapshot; version numbers never renumber, so retention removing older entries does not affect this.
    /// </summary>
    public Task<int> GetMaxConfigurationChangeVersionAsync(ActivityTargetType targetType, int targetObjectId);

    /// <summary>
    /// Gets the highest configuration-change version recorded for a Guid-keyed configuration object (e.g. a
    /// <see cref="ActivityTargetType.Schedule"/>), identified by its activity target type and Guid database id, or 0 if
    /// none exist yet. The Guid-keyed counterpart of <see cref="GetMaxConfigurationChangeVersionAsync(ActivityTargetType,int)"/>.
    /// </summary>
    public Task<int> GetMaxConfigurationChangeVersionAsync(ActivityTargetType targetType, Guid targetObjectId);

    /// <summary>
    /// Gets the snapshot JSON of the highest configuration-change version recorded for a configuration object, or null
    /// if none exists yet. Used by the idempotent capture guard: a new capture whose snapshot is identical to the
    /// latest stored one is skipped rather than recorded as a no-change version.
    /// </summary>
    public Task<string?> GetLatestConfigurationChangeSnapshotAsync(ActivityTargetType targetType, int targetObjectId);

    /// <summary>
    /// Gets the snapshot JSON of the highest configuration-change version recorded for a Guid-keyed configuration
    /// object (e.g. a <see cref="ActivityTargetType.Schedule"/>), or null if none exists yet. The Guid-keyed
    /// counterpart of <see cref="GetLatestConfigurationChangeSnapshotAsync(ActivityTargetType,int)"/>.
    /// </summary>
    public Task<string?> GetLatestConfigurationChangeSnapshotAsync(ActivityTargetType targetType, Guid targetObjectId);

    /// <summary>
    /// Counts the versioned configuration-change activities recorded for a configuration object.
    /// </summary>
    public Task<int> GetConfigurationChangeCountAsync(ActivityTargetType targetType, int targetObjectId);

    /// <summary>
    /// Counts the versioned configuration-change activities recorded for a Guid-keyed configuration object (e.g. a
    /// <see cref="ActivityTargetType.Schedule"/>).
    /// </summary>
    public Task<int> GetConfigurationChangeCountAsync(ActivityTargetType targetType, Guid targetObjectId);

    /// <summary>
    /// Returns a page of versioned configuration-change activities for a configuration object, newest version first,
    /// each including the raw snapshot JSON so the application layer can build summaries and diffs.
    /// </summary>
    public Task<List<ConfigurationChangeActivityData>> GetConfigurationChangeActivitiesAsync(ActivityTargetType targetType, int targetObjectId, int skip, int take);

    /// <summary>
    /// Returns a page of versioned configuration-change activities for a Guid-keyed configuration object, newest
    /// version first. The Guid-keyed counterpart of
    /// <see cref="GetConfigurationChangeActivitiesAsync(ActivityTargetType,int,int,int)"/>.
    /// </summary>
    public Task<List<ConfigurationChangeActivityData>> GetConfigurationChangeActivitiesAsync(ActivityTargetType targetType, Guid targetObjectId, int skip, int take);

    /// <summary>
    /// Returns the configuration-change activity for a specific version of a configuration object, or null if absent.
    /// </summary>
    public Task<ConfigurationChangeActivityData?> GetConfigurationChangeActivityByVersionAsync(ActivityTargetType targetType, int targetObjectId, int version);

    /// <summary>
    /// Returns the configuration-change activity for a specific version of a Guid-keyed configuration object, or null
    /// if absent.
    /// </summary>
    public Task<ConfigurationChangeActivityData?> GetConfigurationChangeActivityByVersionAsync(ActivityTargetType targetType, Guid targetObjectId, int version);

    /// <summary>
    /// Returns the configuration-change activity for the highest version below <paramref name="version"/> (the
    /// immediate predecessor), or null if none exists. Used to diff a version against the one before it.
    /// </summary>
    public Task<ConfigurationChangeActivityData?> GetConfigurationChangeActivityBeforeVersionAsync(ActivityTargetType targetType, int targetObjectId, int version);

    /// <summary>
    /// Returns the configuration-change activity for the highest version below <paramref name="version"/> of a
    /// Guid-keyed configuration object, or null if none exists.
    /// </summary>
    public Task<ConfigurationChangeActivityData?> GetConfigurationChangeActivityBeforeVersionAsync(ActivityTargetType targetType, Guid targetObjectId, int version);

    /// <summary>
    /// Gets the highest configuration-change version recorded for a string-keyed configuration object (e.g. a
    /// <see cref="ActivityTargetType.ServiceSetting"/>, keyed by its setting key), or 0 if none exist yet. The
    /// string-keyed counterpart of <see cref="GetMaxConfigurationChangeVersionAsync(ActivityTargetType,int)"/>.
    /// </summary>
    public Task<int> GetMaxConfigurationChangeVersionAsync(ActivityTargetType targetType, string targetObjectKey);

    /// <summary>
    /// Gets the snapshot JSON of the highest configuration-change version recorded for a string-keyed configuration
    /// object (e.g. a <see cref="ActivityTargetType.ServiceSetting"/>), or null if none exists yet. The string-keyed
    /// counterpart of <see cref="GetLatestConfigurationChangeSnapshotAsync(ActivityTargetType,int)"/>.
    /// </summary>
    public Task<string?> GetLatestConfigurationChangeSnapshotAsync(ActivityTargetType targetType, string targetObjectKey);

    /// <summary>
    /// Counts the versioned configuration-change activities recorded for a string-keyed configuration object (e.g. a
    /// <see cref="ActivityTargetType.ServiceSetting"/>).
    /// </summary>
    public Task<int> GetConfigurationChangeCountAsync(ActivityTargetType targetType, string targetObjectKey);

    /// <summary>
    /// Returns a page of versioned configuration-change activities for a string-keyed configuration object, newest
    /// version first. The string-keyed counterpart of
    /// <see cref="GetConfigurationChangeActivitiesAsync(ActivityTargetType,int,int,int)"/>.
    /// </summary>
    public Task<List<ConfigurationChangeActivityData>> GetConfigurationChangeActivitiesAsync(ActivityTargetType targetType, string targetObjectKey, int skip, int take);

    /// <summary>
    /// Returns the configuration-change activity for a specific version of a string-keyed configuration object, or
    /// null if absent.
    /// </summary>
    public Task<ConfigurationChangeActivityData?> GetConfigurationChangeActivityByVersionAsync(ActivityTargetType targetType, string targetObjectKey, int version);

    /// <summary>
    /// Returns the configuration-change activity for the highest version below <paramref name="version"/> of a
    /// string-keyed configuration object, or null if none exists.
    /// </summary>
    public Task<ConfigurationChangeActivityData?> GetConfigurationChangeActivityBeforeVersionAsync(ActivityTargetType targetType, string targetObjectKey, int version);

    /// <summary>
    /// Queries the database for RPEI error counts for an activity, returning the total number of
    /// RPEIs with errors, the total number of RPEIs, and the number of UnhandledError RPEIs.
    /// Used to determine activity completion status (success/warning/failure) without loading
    /// RPEIs into memory.
    /// </summary>
    public Task<(int TotalWithErrors, int TotalRpeis, int TotalUnhandledErrors)> GetActivityRpeiErrorCountsAsync(Guid activityId);

    /// <summary>
    /// Atomically increments <c>AttemptCount</c> and advances <c>LastSeen</c> on the aggregated failed-authentication
    /// Activity row matching (TargetType Authentication, <paramref name="apiKeyPrefix"/>, <paramref name="clientIp"/>,
    /// <paramref name="reason"/>, <paramref name="windowStart"/>). Callers must normalise a null API key prefix or
    /// client IP to <see cref="string.Empty"/> before calling, matching the partial unique index's dedup contract
    /// (Postgres unique indexes treat NULLs as distinct from one another).
    /// </summary>
    /// <returns>True if a matching row was found and incremented; false if no row exists yet for this window bucket
    /// (the caller must then create one).</returns>
    public Task<bool> IncrementAggregatedFailedAuthenticationAsync(string apiKeyPrefix, string clientIp, string reason, DateTime windowStart, DateTime lastSeen);

    /// <summary>
    /// Returns, for each of the given Connected Systems that has one, the <see cref="Activity.Executed"/> time of its
    /// most recent successfully completed Full Synchronisation. Systems that have never completed one are absent from
    /// the dictionary rather than carrying a sentinel date, so callers must distinguish "never" from "long ago".
    ///
    /// Runs that failed, errored or were cancelled do not count: a Full Synchronisation that did not finish cleanly
    /// cannot be relied on to have applied the configuration, so treating it as a reference point would hide real
    /// pending changes.
    /// </summary>
    public Task<Dictionary<int, DateTime>> GetLastFullSynchronisationStartsAsync(IList<int> connectedSystemIds);

    /// <summary>
    /// Returns the target columns and classification of every configuration change recorded at or after
    /// <paramref name="since"/> whose class is at least <paramref name="minimumClass"/>, for the caller to attribute
    /// to the Connected Systems it affects.
    ///
    /// Unlike the per-object configuration history queries, this deliberately does not require a captured version:
    /// deletions are recorded without one and are precisely the changes that most need surfacing.
    /// </summary>
    public Task<List<ConfigurationChangeImpactData>> GetConfigurationChangeImpactsSinceAsync(DateTime since, ConfigurationChangeClass minimumClass);
}