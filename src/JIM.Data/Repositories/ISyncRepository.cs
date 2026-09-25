// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;
using JIM.Models.Utility;

namespace JIM.Data.Repositories;

/// <summary>
/// Consolidated data access boundary for all worker sync operations.
/// <para>
/// This interface encapsulates every I/O operation that sync processors (import, sync, export)
/// need during a synchronisation run. In production, the implementation uses raw SQL/Npgsql
/// for hot-path operations. In tests, a purpose-built in-memory implementation provides
/// deterministic behaviour without EF Core quirks.
/// </para>
/// <para>
/// This replaces the current pattern where sync processors call through JimApplication's
/// 17 server properties (ConnectedSystems, Metaverse, Activities, etc.) and directly access
/// the repository layer, which caused three-way code path divergence between production,
/// workflow tests, and unit tests.
/// </para>
/// </summary>
/// <remarks>
/// Design decisions:
/// <list type="bullet">
/// <item>
/// This interface is PURE DATA ACCESS. Business logic (object matching, CSO caching, settings,
/// RPEI-linking, connector triad operations, activity failure) lives on <c>ISyncServer</c>.
/// </item>
/// <item>
/// Change tracker management (clear, auto-detect toggle) IS included because sync processors
/// need explicit control during batch page processing.
/// </item>
/// </list>
/// </remarks>
public interface ISyncRepository
{
    #region Connected System Object — Reads

    /// <summary>
    /// Gets the total number of CSOs for a Connected System.
    /// Used to calculate page count at sync start.
    /// </summary>
    Task<int> GetConnectedSystemObjectCountAsync(int connectedSystemId, int? partitionId = null);

    /// <summary>
    /// Gets the count of CSOs modified since the specified date.
    /// Used by delta sync to calculate page count.
    /// </summary>
    Task<int> GetConnectedSystemObjectModifiedSinceCountAsync(int connectedSystemId, DateTime modifiedSince);

    /// <summary>
    /// Loads a page of CSOs with full attribute values for sync processing.
    /// </summary>
    /// <param name="knownTotalCount">When provided, skips the per-page COUNT query and uses this value
    /// for paging metadata. Callers that already know the total (e.g. full sync) should pass it to
    /// eliminate redundant COUNT(*) queries at scale.</param>
    /// <param name="afterId">Keyset cursor: when provided, returns the page of CSOs whose ID sorts
    /// after this value (in the database engine's ordering) instead of using OFFSET, keeping every
    /// page O(pageSize) at scale. Sequential callers must pass the ID of the last row of the previous
    /// page exactly as returned. Null behaves as the offset-based page requested via
    /// <paramref name="page"/>.</param>
    Task<PagedResultSet<ConnectedSystemObject>> GetConnectedSystemObjectsAsync(int connectedSystemId, int page, int pageSize, int? knownTotalCount = null, DateTime? lastSyncTimestamp = null, Guid? afterId = null);

    /// <summary>
    /// Loads a page of CSOs modified since the specified date, with full attribute values.
    /// Used by delta sync to process only recently changed objects.
    /// </summary>
    /// <param name="knownTotalCount">When provided, skips the per-page COUNT query and uses this value
    /// for paging metadata.</param>
    Task<PagedResultSet<ConnectedSystemObject>> GetConnectedSystemObjectsModifiedSinceAsync(int connectedSystemId, DateTime modifiedSince, int page, int pageSize, int? knownTotalCount = null);

    /// <summary>
    /// Gets a single CSO by ID with full attribute values.
    /// Used for cross-page reference resolution and lazy attribute loading.
    /// </summary>
    Task<ConnectedSystemObject?> GetConnectedSystemObjectAsync(int connectedSystemId, Guid csoId);

    /// <summary>
    /// Gets a CSO by its external ID attribute value (int type).
    /// Used during import to match incoming objects to existing CSOs.
    /// </summary>
    Task<ConnectedSystemObject?> GetConnectedSystemObjectByAttributeAsync(int connectedSystemId, int attributeId, int attributeValue);

    /// <summary>
    /// Gets a CSO by its external ID attribute value (string type).
    /// </summary>
    Task<ConnectedSystemObject?> GetConnectedSystemObjectByAttributeAsync(int connectedSystemId, int attributeId, string attributeValue);

    /// <summary>
    /// Gets a CSO by its external ID attribute value (Guid type).
    /// </summary>
    Task<ConnectedSystemObject?> GetConnectedSystemObjectByAttributeAsync(int connectedSystemId, int attributeId, Guid attributeValue);

    /// <summary>
    /// Gets a CSO by its external ID attribute value (long type).
    /// </summary>
    Task<ConnectedSystemObject?> GetConnectedSystemObjectByAttributeAsync(int connectedSystemId, int attributeId, long attributeValue);

    /// <summary>
    /// Gets a Connected System Object by a decimal attribute value. Oracle's <c>NUMBER</c> is discovered
    /// as Decimal, so this covers the ordinary sequence-backed primary key on that provider (#1283).
    /// Matching is numeric, so a stored 4200.00 matches a supplied 4200.
    /// </summary>
    Task<ConnectedSystemObject?> GetConnectedSystemObjectByAttributeAsync(int connectedSystemId, int attributeId, decimal attributeValue);

    /// <summary>
    /// Gets a CSO by its secondary external ID attribute value.
    /// Used during confirming imports to match exported objects.
    /// </summary>
    Task<ConnectedSystemObject?> GetConnectedSystemObjectBySecondaryExternalIdAsync(int connectedSystemId, int objectTypeId, string secondaryExternalIdValue);

    /// <summary>
    /// Gets a CSO by secondary external ID searching across all object types.
    /// </summary>
    Task<ConnectedSystemObject?> GetConnectedSystemObjectBySecondaryExternalIdAnyTypeAsync(int connectedSystemId, string secondaryExternalIdValue);

    /// <summary>
    /// Bulk-loads all CSO external ID mappings for a Connected System into a lightweight dictionary.
    /// Returns a dictionary mapping cache keys ("cso:{connectedSystemId}:{attributeId}:{lowerExternalIdValue}")
    /// to CSO GUIDs. Used as the lookup phase of the import pipeline to answer "does this CSO exist?"
    /// in O(1) without loading full entity graphs.
    /// </summary>
    Task<Dictionary<string, Guid>> GetAllCsoExternalIdMappingsAsync(int connectedSystemId);

    /// <summary>
    /// SPEC-1082 D8: bulk-loads all CSO import state for a Connected System into a lightweight
    /// dictionary, keyed by the same composite cache key as <see cref="GetAllCsoExternalIdMappingsAsync"/>.
    /// Widens that method's projection with the stored content hash, schema fingerprint, status,
    /// and partition, so a Full Import can evaluate the SPEC-1082 skip predicate for every matched
    /// object without any additional database round trip.
    /// </summary>
    Task<Dictionary<string, CsoImportStateLookupEntry>> GetAllCsoImportStateLookupAsync(int connectedSystemId);

    /// <summary>
    /// SPEC-1082 D6: the ONLY code path permitted to write <see cref="ConnectedSystemObject.ImportStateHash"/>
    /// and <see cref="ConnectedSystemObject.ImportStateFingerprint"/>. Callers MUST invoke this
    /// strictly after the batch's attribute-value writes for the given CSOs have committed
    /// (stamp-ordering invariant): stamping before the values it describes have committed would let
    /// a crash between the two leave a hash that lies about the CSO's content. Does NOT touch
    /// <see cref="ConnectedSystemObject.LastUpdated"/> (the #891 Full Synchronisation watermark).
    /// A no-op for an empty list.
    /// </summary>
    Task StampImportStateAsync(IReadOnlyCollection<(Guid CsoId, Guid? Hash, Guid? Fingerprint)> stamps);

    /// <summary>
    /// Batch-loads full CSO entity graphs by their IDs.
    /// Returns CSOs with Type.Attributes and AttributeValues.Attribute populated (CSOs of the same
    /// type sharing one Type instance); ReferenceValue navigations are deliberately NOT loaded (#917).
    /// Used as the hydration phase of the import pipeline after the lookup phase identifies which CSOs exist.
    /// </summary>
    Task<List<ConnectedSystemObject>> GetConnectedSystemObjectsByIdsAsync(int connectedSystemId, IEnumerable<Guid> csoIds);

    /// <summary>
    /// Loads CSOs by ID with AttributeValues using AsNoTracking, for reconciliation comparisons.
    /// </summary>
    Task<List<ConnectedSystemObject>> GetConnectedSystemObjectsByIdsNoTrackingAsync(int connectedSystemId, IEnumerable<Guid> csoIds);

    /// <summary>
    /// Summary-tier lookup of the external ID and object type name for the given CSOs, keyed by CSO ID.
    /// Projects only the external-ID attribute value (correlated scalar subqueries keyed on the CSO's
    /// external-ID attribute) and the type name, never the full attribute-value collection, so it stays
    /// cheap even for large-membership group CSOs. Used to snapshot reference-recall RPEIs at end of run.
    /// </summary>
    Task<Dictionary<Guid, ConnectedSystemObjectDisplaySnapshot>> GetConnectedSystemObjectDisplaySnapshotsAsync(IReadOnlyCollection<Guid> csoIds);

    /// <summary>
    /// Gets multiple CSOs by their external ID attribute values (batch lookup).
    /// Returns a dictionary keyed by the string representation of the attribute value.
    /// Used during import to batch-match incoming objects.
    /// </summary>
    Task<Dictionary<string, ConnectedSystemObject>> GetConnectedSystemObjectsByAttributeValuesAsync(int connectedSystemId, int attributeId, IEnumerable<string> attributeValues);

    /// <summary>
    /// Gets multiple CSOs by secondary external ID values (batch lookup, any object type).
    /// Returns a dictionary keyed by the secondary external ID value.
    /// </summary>
    Task<Dictionary<string, ConnectedSystemObject>> GetConnectedSystemObjectsBySecondaryExternalIdAnyTypeValuesAsync(int connectedSystemId, IEnumerable<string> secondaryExternalIdValues);

    /// <summary>
    /// Gets all external ID attribute values of type int for a Connected System and object type.
    /// Used during import to build the full set of known external IDs for deletion detection.
    /// </summary>
    Task<List<int>> GetAllExternalIdAttributeValuesOfTypeIntAsync(int connectedSystemId, int objectTypeId, int? partitionId = null);

    /// <summary>
    /// Gets all external ID attribute values of type string.
    /// </summary>
    Task<List<string>> GetAllExternalIdAttributeValuesOfTypeStringAsync(int connectedSystemId, int objectTypeId, int? partitionId = null);

    /// <summary>
    /// Gets all external ID attribute values of type Guid.
    /// </summary>
    Task<List<Guid>> GetAllExternalIdAttributeValuesOfTypeGuidAsync(int connectedSystemId, int objectTypeId, int? partitionId = null);

    /// <summary>
    /// Gets all external ID attribute values of type long.
    /// </summary>
    Task<List<long>> GetAllExternalIdAttributeValuesOfTypeLongAsync(int connectedSystemId, int objectTypeId, int? partitionId = null);

    /// <summary>
    /// Gets all external ID attribute values of type decimal. Oracle's <c>NUMBER</c> is discovered as
    /// Decimal, so this covers the ordinary sequence-backed primary key on that provider (#1283).
    /// Equal decimals hash equally regardless of scale, so the caller may set-compare these directly.
    /// </summary>
    Task<List<decimal>> GetAllExternalIdAttributeValuesOfTypeDecimalAsync(int connectedSystemId, int objectTypeId, int? partitionId = null);

    /// <summary>
    /// Returns every Pending Export for the given Connected System Object Type (and optionally
    /// partition) that is a Create, Status Exported, targeting a Connected System Object still Status
    /// PendingProvisioning: an exported Create whose confirming import has not yet reported the object
    /// back. Ordinary deletion detection excludes PendingProvisioning Connected System Objects outright
    /// (they have no External ID yet to compare), so this is a separate, deliberately narrow query used
    /// only by a Full Import's "unseen exported Create" retry step
    /// (<see cref="JIM.Application.Interfaces.ISyncEngine.IsExportedCreateUnseenByFullImport"/>): the
    /// caller compares each returned Pending Export's Connected System Object External Id against the
    /// run's own imported set to decide whether the Create was genuinely unseen.
    /// <para>
    /// Loads the full graph, so the retry step calls this only for candidates the lean
    /// <see cref="GetExportedCreatePendingExportRetryCandidateSummariesAsync"/> projection has already
    /// decided are genuinely unseen: <paramref name="pendingExportIds"/> narrows this same eligibility
    /// query to exactly those, rather than re-loading every candidate a second time.
    /// </para>
    /// </summary>
    /// <param name="pendingExportIds">When supplied, restricts the result to these Pending Export ids
    /// (still subject to every other filter above). Null loads every eligible candidate, as before.</param>
    Task<List<PendingExport>> GetExportedCreatePendingExportsForPendingProvisioningCsosAsync(int connectedSystemId, int objectTypeId, int? partitionId = null, IReadOnlyCollection<Guid>? pendingExportIds = null);

    /// <summary>
    /// Lean, Summary-tier equivalent of <see cref="GetExportedCreatePendingExportsForPendingProvisioningCsosAsync"/>:
    /// identical eligibility (Connected System, Create, Status Exported, Connected System Object not null
    /// and Pending Provisioning, Object Type, optional partition), but returns only the Pending Export id,
    /// the Connected System Object id, and the Connected System Object's primary External Id value as
    /// typed nullable columns - never the full Pending Export / attribute-change / Connected System
    /// Object / attribute-value graph. A Full Import's unseen exported-Create retry step uses this to
    /// decide, for every candidate, whether the run saw the object, and only loads the full graph for the
    /// (usually far smaller, often empty) subset genuinely unseen.
    /// </summary>
    Task<List<PendingExportRetryCandidateSummary>> GetExportedCreatePendingExportRetryCandidateSummariesAsync(int connectedSystemId, int objectTypeId, int? partitionId = null);

    /// <summary>
    /// Loads CSOs by ID for cross-page reference resolution.
    /// Only loads CSOs and their attribute values — no navigation properties beyond that.
    /// </summary>
    Task<List<ConnectedSystemObject>> GetConnectedSystemObjectsForReferenceResolutionAsync(IList<Guid> csoIds);

    /// <summary>
    /// Gets the string representations of reference attribute values for a CSO.
    /// Used during cross-page reference resolution to find unresolved references.
    /// </summary>
    Task<Dictionary<Guid, string>> GetReferenceExternalIdsAsync(Guid csoId);

    /// <summary>
    /// Batched form of <see cref="GetReferenceExternalIdsAsync"/>: gets the reference external ID
    /// lookups for a whole page of CSOs in one query, keyed by owning CSO ID. Every requested ID
    /// is present in the result (empty dictionary when the CSO has no resolved references).
    /// Import processing previously issued the single-CSO variant once per existing object
    /// (535K round trips at Scale500k25kGroups); callers with a hydrated page should use this instead.
    /// </summary>
    Task<Dictionary<Guid, Dictionary<Guid, string>>> GetReferenceExternalIdsForCsosAsync(IReadOnlyCollection<Guid> csoIds);

    /// <summary>
    /// Gets the Connected System ID of each CSO joined to a specific MVO across all Connected Systems:
    /// one entry per CSO, duplicates preserved when a system holds multiple joined CSOs.
    /// Used by the MVO deletion rule evaluation to determine both how many connectors remain and which
    /// systems they belong to after a disconnection (#119). Replaces the previous count-only query.
    /// </summary>
    Task<List<int>> GetJoinedConnectedSystemIdsByMetaverseObjectIdAsync(Guid metaverseObjectId);

    /// <summary>
    /// Gets the count of CSOs joined to a specific MVO within a single Connected System.
    /// Used during join validation to check maximum join cardinality.
    /// </summary>
    Task<int> GetConnectedSystemObjectCountByMvoAsync(int connectedSystemId, Guid metaverseObjectId);

    #endregion

    #region Connected System Object — Writes

    /// <summary>
    /// Bulk creates CSOs with their attribute values.
    /// Uses raw SQL bulk operations in production for performance.
    /// </summary>
    /// <param name="connectedSystemObjects">CSOs to create.</param>
    /// <param name="previouslyCommittedCsoIds">IDs of CSOs already committed in prior batches.
    /// When set, ReferenceValueId FKs pointing to these IDs are preserved (not nulled) during
    /// bulk insert, eliminating the need for post-hoc FixupCrossBatchReferenceIdsAsync.</param>
    Task CreateConnectedSystemObjectsAsync(List<ConnectedSystemObject> connectedSystemObjects, HashSet<Guid>? previouslyCommittedCsoIds = null);

    /// <summary>
    /// Bulk updates CSOs with their attribute values.
    /// Uses raw SQL bulk operations in production for performance.
    /// </summary>
    Task UpdateConnectedSystemObjectsAsync(
        List<ConnectedSystemObject> connectedSystemObjects,
        List<(Guid CsoId, ConnectedSystemObjectAttributeValue Value)>? pendingAdditions = null,
        List<Guid>? pendingRemovalIds = null);

    /// <summary>
    /// Updates only the join state fields (MetaverseObjectId, JoinType, Status) on CSOs
    /// without touching their attribute values. Used after join/projection operations.
    /// </summary>
    Task UpdateConnectedSystemObjectJoinStatesAsync(List<ConnectedSystemObject> connectedSystemObjects);

    /// <summary>
    /// Clears the <c>ScopeReviewPending</c> flag on CSOs the sync engine has re-evaluated past the unchanged-skip
    /// (issue #892). Called at page flush. No-op when <paramref name="ids"/> is empty.
    /// </summary>
    Task ClearConnectedSystemObjectScopeReviewPendingAsync(IReadOnlyCollection<Guid> ids);

    /// <summary>
    /// Updates CSOs that have new attribute values added (e.g., secondary external ID during export).
    /// </summary>
    Task UpdateConnectedSystemObjectsWithNewAttributeValuesAsync(List<(ConnectedSystemObject cso, List<ConnectedSystemObjectAttributeValue> newAttributeValues)> updates);

    /// <summary>
    /// Persists an optimistic export apply delta (issue #1079): bulk-inserts newly exported
    /// attribute value rows and bulk-deletes superseded ones. Touches NO parent CSO row (Status,
    /// LastUpdated and every other CSO field are left exactly as they were): re-arming the Full
    /// Synchronisation unchanged-object watermark for a no-op confirming import depends on
    /// LastUpdated staying untouched here. Callers are responsible for keeping the in-memory
    /// Connected System Object graph in sync with the persisted delta afterwards.
    /// <para>
    /// SPEC-1082 D9: also nulls <see cref="ConnectedSystemObject.ImportStateHash"/> and
    /// <see cref="ConnectedSystemObject.ImportStateFingerprint"/> for every CSO in
    /// <paramref name="affectedCsoIds"/>, set-based, in the SAME persistence call. This mutates
    /// attribute values outside the Full Import stamp path (D6/D7), so any stored hash describing
    /// the CSO's prior content is no longer trustworthy.
    /// </para>
    /// </summary>
    Task ApplyExportedAttributeValuesAsync(List<ConnectedSystemObjectAttributeValue> additions, List<Guid> removalValueIds, IReadOnlyCollection<Guid> affectedCsoIds);

    /// <summary>
    /// Deletes CSOs and their attribute values without change tracking.
    /// Used for quiet deletions (e.g., pre-disconnected CSOs).
    /// </summary>
    Task DeleteConnectedSystemObjectsAsync(List<ConnectedSystemObject> connectedSystemObjects);

    /// <summary>
    /// Resolves cross-batch reference attribute values that could not be resolved during initial import
    /// because the target CSO was in a later batch. Uses raw SQL JOIN with partial indexes.
    /// Returns the count of resolved references.
    /// </summary>
    Task<int> FixupCrossBatchReferenceIdsAsync(int connectedSystemId);

    /// <summary>
    /// Resolves cross-batch reference values in CSO change records (ConnectedSystemObjectChangeAttributeValues)
    /// that were nulled during COPY binary persistence to avoid FK violations. The DN string is preserved
    /// in StringValue and is matched against the secondary external ID attribute values of CSOs in the
    /// same Connected System using case-insensitive comparison. Resolutions are applied in bounded
    /// batches so each statement stays inside the bulk command timeout regardless of backlog size
    /// (a Scale500k25kGroups run accumulates millions of unresolved rows).
    /// </summary>
    /// <param name="connectedSystemId">The Connected System whose change records to resolve.</param>
    /// <param name="batchSize">Rows updated per statement; null uses the implementation default.</param>
    Task<int> FixupCrossBatchChangeRecordReferenceIdsAsync(int connectedSystemId, int? batchSize = null);

    /// <summary>
    /// Tactical fixup: populates ReferenceValueId on MetaverseObjectAttributeValues where the FK is
    /// null but the referenced MVO exists. EF does not reliably infer the FK from the ReferenceValue
    /// navigation when entities are managed via explicit state (Entry().State = Added/Modified).
    /// This will be retired when MVO persistence is converted to direct SQL.
    /// </summary>
    Task<int> FixupMvoReferenceValueIdsAsync(IReadOnlyList<(Guid MvoId, int AttributeId, Guid TargetMvoId)> fixups);

    #endregion

    #region Object Matching — Data Access

    /// <summary>
    /// Finds an MVO that matches a CSO using the specified matching rule.
    /// Used by object matching during import (CSO→MVO join).
    /// </summary>
    Task<MetaverseObject?> FindMetaverseObjectUsingMatchingRuleAsync(
        ConnectedSystemObject connectedSystemObject,
        MetaverseObjectType metaverseObjectType,
        ObjectMatchingRule objectMatchingRule);

    /// <summary>
    /// Finds a CSO that matches an MVO using the specified matching rule.
    /// Used by object matching during export (MVO→CSO lookup).
    /// </summary>
    Task<ConnectedSystemObject?> FindConnectedSystemObjectUsingMatchingRuleAsync(
        MetaverseObject metaverseObject,
        ConnectedSystem connectedSystem,
        ConnectedSystemObjectType connectedSystemObjectType,
        ObjectMatchingRule objectMatchingRule);

    /// <summary>
    /// Batch equivalent of <see cref="FindConnectedSystemObjectUsingMatchingRuleAsync"/>: for a single
    /// Object Matching Rule, finds every unjoined, Normal-status Connected System Object of the given
    /// type whose named attribute equals one of the given values, in one query per rule per page
    /// instead of one query per Metaverse Object. See <c>IConnectedSystemRepository</c> for full
    /// parameter and eligibility documentation.
    /// </summary>
    Task<IReadOnlyList<(object Value, Guid ConnectedSystemObjectId)>> GetExportMatchCandidateIdsAsync(
        int connectedSystemId,
        int connectedSystemObjectTypeId,
        string connectedSystemAttributeName,
        AttributeDataType dataType,
        bool caseSensitive,
        IReadOnlyCollection<object> values);

    /// <summary>
    /// Hydrates a single export-matching candidate found by <see cref="GetExportMatchCandidateIdsAsync"/>.
    /// See <c>IConnectedSystemRepository</c> for full documentation.
    /// </summary>
    Task<ConnectedSystemObject?> GetConnectedSystemObjectForExportMatchAsync(Guid connectedSystemObjectId);

    #endregion

    #region Metaverse Object — Writes

    /// <summary>
    /// Returns up to <paramref name="maxResults"/> Metaverse Object ids flagged <c>ScopeReviewPending</c> by the
    /// Temporal Scope Reconciler (issue #892), for the sync engine to drain into export re-evaluation.
    /// </summary>
    Task<List<Guid>> GetMetaverseObjectIdsWithScopeReviewPendingAsync(int maxResults);

    /// <summary>
    /// Loads the given Metaverse Objects (no tracking) with their Type and attribute values, for export
    /// re-evaluation of Temporal Scope Reconciler-flagged objects (issue #892).
    /// </summary>
    Task<List<MetaverseObject>> GetMetaverseObjectsByIdsNoTrackingAsync(IEnumerable<Guid> ids);

    /// <summary>
    /// As <see cref="GetMetaverseObjectsByIdsNoTrackingAsync"/> but TRACKED, for callers that mutate and
    /// persist the loaded graph in the same context; see <c>IMetaverseRepository</c> for why the
    /// no-tracking variant cannot be persisted alongside tracked loads.
    /// </summary>
    Task<List<MetaverseObject>> GetMetaverseObjectsByIdsForUpdateAsync(IEnumerable<Guid> ids);

    /// <summary>
    /// The distinct ids of Metaverse Objects holding at least one attribute value contributed by the given
    /// Synchronisation Rule (selected by provenance, <c>ContributedBySyncRuleId</c>). Drives the rule
    /// deletion recall task (#1537), which must enumerate the affected objects before the rule's deletion
    /// severs the very provenance this selects on.
    /// </summary>
    Task<List<Guid>> GetMetaverseObjectIdsWithValuesContributedBySyncRuleAsync(int syncRuleId);

    /// <summary>
    /// The distinct ids of Metaverse Objects holding at least one attribute value contributed by the given
    /// Synchronisation Rule where the object holds no Connected System Object of the given Connected System:
    /// stranded by an earlier Connector Space clear (#1549). Drives the stranded-value sweep.
    /// </summary>
    /// <param name="syncRuleId">The Synchronisation Rule whose contributed values select the candidate objects.</param>
    /// <param name="connectedSystemId">The Connected System whose Connector Space was cleared.</param>
    Task<List<Guid>> GetMetaverseObjectIdsWithStrandedValuesContributedBySyncRuleAsync(int syncRuleId, int connectedSystemId);

    /// <summary>
    /// The cached display names of the given Metaverse Objects, keyed by id, for naming a referenced
    /// object in an export's unresolved-reference message without loading it (issue #1398). Objects
    /// that do not exist are absent from the result.
    /// </summary>
    Task<Dictionary<Guid, string?>> GetMetaverseObjectDisplayNamesAsync(IReadOnlyCollection<Guid> ids);

    /// <summary>
    /// Clears the <c>ScopeReviewPending</c> flag on Metaverse Objects the sync engine has re-evaluated for export
    /// scope (issue #892). No-op when <paramref name="ids"/> is empty.
    /// </summary>
    Task ClearMetaverseObjectScopeReviewPendingAsync(IReadOnlyCollection<Guid> ids);

    /// <summary>
    /// Returns the reference attribute values held by OTHER Metaverse Objects that point at any of the given
    /// Metaverse Objects. Called before deleting the given objects so reference recall can stage
    /// membership-removal Pending Exports for the referencing objects (issue #908); the deletion path nulls
    /// the reference FKs, after which the linkage is unrecoverable. References held by objects that are
    /// themselves in <paramref name="referencedMetaverseObjectIds"/> are excluded.
    /// </summary>
    Task<List<MvoReferenceRecallCandidate>> GetMetaverseObjectReferenceRecallCandidatesAsync(
        IReadOnlyCollection<Guid> referencedMetaverseObjectIds);

    /// <summary>
    /// Summary-tier load of referencing Metaverse Objects for reference recall staging (#1003):
    /// id, type id and display name, plus only the attribute values whose attribute ids are in
    /// <paramref name="scopingAttributeIds"/> (scalar columns and the asserted-null marker; no
    /// navigations). Never materialises the objects' full attribute graphs.
    /// </summary>
    Task<List<MetaverseObjectRecallSummary>> GetMetaverseObjectRecallSummariesAsync(
        IReadOnlyCollection<Guid> metaverseObjectIds,
        IReadOnlyCollection<int> scopingAttributeIds);

    /// <summary>
    /// Summary-tier load of the Connected System Objects joined to the given Metaverse Objects in
    /// the given target systems, for reference recall staging (#1003). Scalars only; no attribute
    /// values, no entity materialisation into the change tracker.
    /// </summary>
    Task<List<ConnectedSystemObjectRecallTarget>> GetConnectedSystemObjectRecallTargetsAsync(
        IReadOnlyCollection<Guid> metaverseObjectIds,
        IReadOnlyCollection<int> targetConnectedSystemIds);

    /// <summary>
    /// The reference recall existence query (#1003): returns the Connected System Object attribute
    /// value rows among <paramref name="connectedSystemObjectIds"/> x <paramref name="connectedSystemAttributeIds"/>
    /// that reference a deleted object, matched by resolved reference
    /// (<paramref name="deletedReferenceCsoIds"/>) or by case-insensitive raw reference string
    /// (<paramref name="loweredReferenceValues"/>, pre-lowered with ToLowerInvariant to mirror the
    /// OrdinalIgnoreCase DN comparison export evaluation uses). Call per target Connected System so
    /// values cannot cross-match between systems. Rows not returned are values the target does not
    /// hold; staging nothing for them replaces no-net-change detection.
    /// </summary>
    Task<List<CsoReferenceValueMatch>> GetCsoReferenceValueMatchesAsync(
        IReadOnlyCollection<Guid> connectedSystemObjectIds,
        IReadOnlyCollection<int> connectedSystemAttributeIds,
        IReadOnlyCollection<Guid> deletedReferenceCsoIds,
        IReadOnlyCollection<string> loweredReferenceValues);

    /// <summary>
    /// Bulk creates MVOs with their attribute values.
    /// </summary>
    Task CreateMetaverseObjectsAsync(IEnumerable<MetaverseObject> metaverseObjects);

    /// <summary>
    /// Bulk updates MVOs with their attribute values.
    /// </summary>
    Task UpdateMetaverseObjectsAsync(IEnumerable<MetaverseObject> metaverseObjects);

    /// <summary>
    /// Updates a single MVO. Used for ad-hoc updates outside of batch processing
    /// (e.g., disconnection handling).
    /// </summary>
    Task UpdateMetaverseObjectAsync(MetaverseObject metaverseObject);

    /// <summary>
    /// Deletes an MVO, cascading FK cleanup via raw SQL to prevent constraint violations.
    /// </summary>
    Task DeleteMetaverseObjectAsync(MetaverseObject metaverseObject);

    /// <summary>
    /// Deletes multiple MVOs in one set-based pass: each FK cleanup statement runs once for the
    /// whole batch (<c>= ANY</c>) instead of once per object, and the MVO rows are removed in a
    /// single SaveChanges. Semantically equivalent to calling
    /// <see cref="DeleteMetaverseObjectAsync"/> per object; exists because the per-object form
    /// costs six sequential round trips per MVO, which dominates 0-grace-period deprovisioning
    /// flushes at scale (issue #993).
    /// </summary>
    Task DeleteMetaverseObjectsAsync(IReadOnlyCollection<MetaverseObject> metaverseObjects);

    /// <summary>
    /// Deletes MVO attribute values by their IDs using raw SQL.
    /// Used during cross-page reference resolution where the change tracker is cleared between
    /// batches — EF cannot infer collection removals after clearing, so deletions must be explicit.
    /// </summary>
    Task DeleteMetaverseObjectAttributeValuesByIdsAsync(IReadOnlyList<Guid> attributeValueIds);

    #endregion

    #region Pending Exports

    /// <summary>
    /// Gets all Pending Exports for a Connected System.
    /// Used at sync start to build the O(1) Pending Export lookup by CSO ID.
    /// </summary>
    Task<List<PendingExport>> GetPendingExportsAsync(int connectedSystemId);

    /// <summary>
    /// Retrieves the Pending Exports for a Connected System that are awaiting deferred
    /// reference resolution: Pending status with unresolved reference attribute values.
    /// The predicate is evaluated in SQL (backed by a partial index on
    /// HasUnresolvedReferences) so the common zero-deferred case costs a single
    /// index probe rather than hydrating every Pending Export for the system (#1102).
    /// </summary>
    Task<List<PendingExport>> GetPendingExportsWithUnresolvedReferencesAsync(int connectedSystemId);

    /// <summary>
    /// Gets the count of Pending Exports for a Connected System.
    /// Used by the export processor to determine paging.
    /// </summary>
    Task<int> GetPendingExportsCountAsync(int connectedSystemId);

    /// <summary>
    /// Bulk creates Pending Exports with their attribute value changes.
    /// Uses raw SQL bulk operations in production for performance.
    /// </summary>
    Task CreatePendingExportsAsync(IEnumerable<PendingExport> pendingExports);

    /// <summary>
    /// Names the Run Profile Execution Item that queued each Pending Export, after both exist (#1223).
    /// </summary>
    /// <remarks>
    /// The ordinary outbound path stamps <see cref="PendingExport.QueuedByRunProfileExecutionItemId"/> before
    /// the exports are persisted, but the deletion-cascade and reference-recall paths run the other way round:
    /// their Pending Exports are staged and persisted first, and the execution item that reports each one (the
    /// deletion item, the standalone cascade item, the recall item) is only built afterwards. This is the
    /// set-once fix-up for those paths; the caller must also set the property on its in-memory Pending Export
    /// instances so any tracked instance matches the row.
    /// </remarks>
    /// <param name="stamps">The Pending Export ids and the execution item id that queued each.</param>
    Task SetPendingExportQueueingItemsAsync(
        IReadOnlyCollection<(Guid PendingExportId, Guid QueuedByRunProfileExecutionItemId)> stamps);

    /// <summary>
    /// Gets the initial-password configuration of the given Synchronisation Rules, keyed by rule.
    /// <para>
    /// Read on its own rather than through a Synchronisation Rule, which materialises Attribute Flows, Object
    /// Matching Rules and both object types; none of that has anything to say about a password.
    /// </para>
    /// </summary>
    Task<Dictionary<int, SyncRuleInitialPassword>> GetInitialPasswordConfigurationsAsync(IReadOnlyCollection<int> syncRuleIds);

    /// <summary>
    /// Gets the password policy JIM last discovered on a Connected System, or null where none was discovered.
    /// </summary>
    Task<ConnectedSystemPasswordPolicy?> GetDiscoveredPasswordPolicyAsync(int connectedSystemId);

    #region Password Synchronisation queue (#1119)

    /// <summary>
    /// Queues password changes, coalescing each onto any change already owed to the same Connected System for
    /// the same identity (requirement 8).
    /// <para>
    /// Coalescing is the point: the queue holds the latest intended password per target, never a replayable
    /// sequence of historical ones. A person who changes their password twice must not have the older one
    /// delivered to a system that was briefly unavailable.
    /// </para>
    /// </summary>
    Task QueuePasswordChangesAsync(IEnumerable<PendingPasswordChange> changes);

    /// <summary>
    /// Offers <see cref="PendingPasswordChangeOrigin.Provisioned"/> changes to the queue, one per newly
    /// provisioned account, coalescing on the same (Metaverse Object, Connected System) key as
    /// <see cref="QueuePasswordChangesAsync"/> but under a narrower conflict clause (#1697): a provisioned row's
    /// first password must never overwrite the person's real one.
    /// <para>
    /// An existing row is replaced only when it carries nothing worth keeping: it is itself
    /// <see cref="PendingPasswordChangeOrigin.Provisioned"/> (the account was deleted and re-provisioned), or it
    /// is <see cref="PendingPasswordChangeStatus.Expired"/> or <see cref="PendingPasswordChangeStatus.Cancelled"/>
    /// (a dead password that must not block the account's first one). Anything else, a Pending, Delivering or
    /// Parked row of Explicit or Propagated origin, is the person's real password already on its way, or waiting
    /// on a person to fix it, and wins: the offered change is discarded.
    /// </para>
    /// <para>
    /// A discard releases any propagated row found waiting on this exact account: its
    /// <see cref="PendingPasswordChange.NextRetryAt"/> is cleared so a change that was held for the account to
    /// exist is attempted on the very next delivery pass, now that it does.
    /// </para>
    /// </summary>
    Task<List<ProvisionedPasswordStagingOutcome>> StageProvisionedPasswordChangesAsync(IReadOnlyCollection<PendingPasswordChange> changes);

    /// <summary>
    /// Creates Activities in one batch, for callers recording several at once (#1697: one parent Activity per
    /// provisioned password change staged in a batch). No navigation properties are expected to be set, so this
    /// walks nothing beyond the Activities themselves.
    /// </summary>
    Task CreateActivitiesAsync(IReadOnlyCollection<Activity> activities);

    /// <summary>
    /// The password changes owed to a Connected System that are due for a delivery attempt now: pending, and
    /// either never attempted or past their scheduled retry. Oldest first, capped at <paramref name="maximum"/>.
    /// A read with no side effect; delivery itself goes through <see cref="ClaimDuePasswordChangesAsync"/>.
    /// </summary>
    Task<List<PendingPasswordChange>> GetDuePasswordChangesAsync(int connectedSystemId, DateTime asOf, int maximum);

    /// <summary>
    /// Takes the password changes due on a Connected System for delivery (#1635): in one statement, selects the
    /// due rows (pending and due, or claimed under a lease that has run out) with <c>FOR UPDATE SKIP LOCKED</c>,
    /// marks them <see cref="PendingPasswordChangeStatus.Delivering"/> stamped with <paramref name="claimedBy"/>
    /// and <paramref name="asOf"/>, and returns them. Oldest first, capped at <paramref name="maximum"/>.
    /// <para>
    /// Two deliverers claiming the same system at once split the rows between them with no overlap; that is what
    /// <c>SKIP LOCKED</c> is for, and why the select and the update are one statement rather than a read followed
    /// by a write. A claim older than <paramref name="lease"/> is treated as abandoned and claimed again.
    /// </para>
    /// </summary>
    /// <param name="excludePropagated">
    /// True to claim every change but <see cref="PendingPasswordChangeOrigin.Propagated"/> ones: what a lane asks
    /// over a system whose Password Synchronisation is unconfigured or switched off, where propagated changes are
    /// held and an administrator's explicit set or a provisioned account's first password is delivered anyway
    /// (#1635, decision D1; widened for provisioned passwords by #1697, decision D1). False claims every
    /// origin.
    /// </param>
    Task<List<PendingPasswordChange>> ClaimDuePasswordChangesAsync(int connectedSystemId, string claimedBy, DateTime asOf, TimeSpan lease, int maximum, bool excludePropagated);

    /// <summary>
    /// Gives claimed changes back unattempted, returning how many were released. For a lane that claimed and then
    /// could not deliver at all (no password capability, channel refused, cancelled before reaching them): the
    /// rows go back to Pending exactly as they were, with nothing counted against them. Only rows still
    /// <see cref="PendingPasswordChangeStatus.Delivering"/> are touched; one that was cancelled or superseded
    /// meanwhile keeps that outcome.
    /// </summary>
    Task<int> ReleasePasswordChangeClaimsAsync(IEnumerable<Guid> ids);

    /// <summary>
    /// Which Connected Systems have password changes due now, so the Password Delivery Service runs a lane only
    /// for the systems that have work rather than for every configured one. Includes systems holding a claim that
    /// has outlived <paramref name="claimLease"/>, since those rows are claimable again.
    /// <para>
    /// A propagated change on a system with Password Synchronisation unconfigured or switched off never makes
    /// that system due, however much has accumulated: a lane claims nothing propagated there, so reporting it
    /// would have the service run a pointless lane on every poll for as long as the system stayed off. Those
    /// changes are not due, they are held; enabling the system is what releases them, and the row update that
    /// does so wakes the service. Any other origin (<see cref="PendingPasswordChangeOrigin.Explicit"/> or
    /// <see cref="PendingPasswordChangeOrigin.Provisioned"/>) makes its system due whatever the configuration says
    /// (#1635, decision D1; widened by #1697), and a lane over such a system claims only those.
    /// </para>
    /// </summary>
    Task<List<int>> GetConnectedSystemIdsWithDuePasswordChangesAsync(DateTime asOf, TimeSpan claimLease);

    /// <summary>
    /// What the Password Delivery Service has ahead of it, in one query: how many changes a lane would attempt
    /// now, how many are waiting out a backoff, and the earliest scheduled attempt still ahead. Read once per
    /// loop iteration to decide how long to sleep, and written into the service's heartbeat. Counts what a lane
    /// would claim: every change on an enabled system, and everything but propagated changes elsewhere.
    /// </summary>
    Task<PasswordQueueDeliveryOutlook> GetPasswordQueueDeliveryOutlookAsync(DateTime asOf, TimeSpan claimLease);

    /// <summary>
    /// Every queue row a password change produced, by the Activity that recorded the change. Rows a newer change
    /// has since superseded are re-pointed at the newer Activity and so are not returned here: they carry a
    /// different password now.
    /// </summary>
    Task<List<PendingPasswordChange>> GetPasswordChangesByActivityAsync(Guid activityId);

    /// <summary>
    /// Records the outcome of a delivery attempt against each change: its status, failure classification, the
    /// target's message, the attempt count, when the next retry falls due, the account it resolved to, and the
    /// end of its claim.
    /// <para>
    /// Written only where the row is still <see cref="PendingPasswordChangeStatus.Delivering"/>. A row that left
    /// that state while the attempt was in flight (cancelled from the queue page, or superseded by a newer
    /// password) keeps what happened to it; the attempt's Activity still records what the target said.
    /// </para>
    /// </summary>
    Task RecordPasswordChangeAttemptsAsync(IEnumerable<PendingPasswordChange> changes);

    /// <summary>
    /// Removes delivered password changes. Success deletes the row: the queue is work outstanding, and the
    /// Activity is the history (requirement 11).
    /// <para>
    /// Only rows still <see cref="PendingPasswordChangeStatus.Delivering"/> or
    /// <see cref="PendingPasswordChangeStatus.Cancelled"/> are removed (#1697, decision D9), mirroring the
    /// guard on <see cref="RecordPasswordChangeAttemptsAsync"/>: a row that left Delivering because it was
    /// superseded or retried mid-flight is Pending again and carries newer work, so the older delivery's success
    /// must not delete it out from under the retry. A Cancelled row whose password nevertheless landed at the
    /// target is still removed; cancellation does not undo a delivery that already happened.
    /// </para>
    /// </summary>
    Task DeletePasswordChangesAsync(IEnumerable<Guid> ids);

    /// <summary>
    /// Marks every pending password change on a Connected System that has outlived its time to live as expired,
    /// returning how many were marked. An expiry is a recorded outcome, never a silent drop (requirement 9). A
    /// change a deliverer holds is left to that deliverer; expiry never touches a Delivering row.
    /// </summary>
    /// <param name="excludePropagated">
    /// True to expire every change but <see cref="PendingPasswordChangeOrigin.Propagated"/> ones: a lane over a
    /// system whose Password Synchronisation is unconfigured or switched off retires the explicit sets and
    /// provisioned first passwords it is there to deliver (#1697 widens this from explicit-only) and leaves
    /// the held propagated changes exactly as they were, to be expired or delivered by the first lane after the
    /// system is switched on, as they always have been (#1635).
    /// </param>
    Task<int> ExpirePasswordChangesAsync(int connectedSystemId, DateTime asOf, bool excludePropagated);

    /// <summary>
    /// Makes every parked password change on a Connected System due again, returning how many were released.
    /// Requirement 3's drain when a system is enabled, and the same mechanic when its settings change.
    /// </summary>
    Task<int> ReleasePasswordChangesForDeliveryAsync(int connectedSystemId);

    /// <summary>
    /// Makes every parked <see cref="PendingPasswordChangeOrigin.Provisioned"/> change against one Synchronisation
    /// Rule due again, returning how many were released (#1697). The Synchronisation Rule counterpart of
    /// <see cref="ReleasePasswordChangesForDeliveryAsync"/>: a rule's initial-password settings park accounts, so
    /// correcting them releases by rule rather than by Connected System.
    /// <para>
    /// The update fires the queue's own NOTIFY trigger, so the Password Delivery Service attempts the released
    /// rows within seconds; the caller does not schedule or wake anything itself, and the Parked filter lives in
    /// the SQL rather than in the caller.
    /// </para>
    /// </summary>
    /// <param name="syncRuleId">The Synchronisation Rule whose parked provisioned accounts to release.</param>
    Task<int> ReleaseParkedProvisionedPasswordChangesAsync(int syncRuleId);

    /// <summary>
    /// How much queued password work on each Connected System is waiting on a person. A system with nothing to
    /// report is absent from the dictionary rather than present with zeroes.
    /// </summary>
    Task<Dictionary<int, PasswordQueueAttention>> GetPasswordQueueAttentionAsync(IReadOnlyCollection<int> connectedSystemIds);

    /// <summary>
    /// Counts the accounts needing a person's attention over their initial password, by Synchronisation Rule, now
    /// that initial passwords are staged onto this queue as <see cref="PendingPasswordChangeOrigin.Provisioned"/>
    /// rows (#1697). The Synchronisation Rule surfaces' counterpart of <see cref="GetPasswordQueueAttentionAsync"/>.
    /// <para>
    /// A rule with nothing outstanding is absent from the result rather than present with zeroes: the rule
    /// surfaces' source of truth for this now that initial passwords are delivered off this queue (#1697).
    /// </para>
    /// </summary>
    Task<Dictionary<int, InitialPasswordAttention>> GetProvisionedPasswordAttentionBySyncRuleAsync(IReadOnlyCollection<int> syncRuleIds);

    /// <summary>
    /// Removes terminal password changes last touched before <paramref name="olderThan"/>, up to
    /// <paramref name="maxRecords"/>, oldest first. Live changes are never removed, however old.
    /// </summary>
    Task<int> DeleteTerminalPasswordChangesAsync(DateTime olderThan, int maxRecords);

    /// <summary>
    /// One window of the Password Synchronisation queue for a list view, with the identity and Connected System
    /// names resolved (requirement 21).
    /// </summary>
    /// <param name="filter">Which changes to list.</param>
    /// <param name="startIndex">The zero-based index of the first row wanted.</param>
    /// <param name="count">How many rows are wanted.</param>
    /// <param name="sortBy">The column to sort by. Unrecognised names fall back to the queued time.</param>
    /// <param name="sortDescending">Whether the sort is descending.</param>
    /// <param name="includeTotalCount">Whether to count the whole match set alongside the window. Counting is
    /// the expensive half of a window read, so a caller that already knows the total passes false and gets a
    /// null total back.</param>
    Task<RangeResultSet<PendingPasswordChangeHeader>> GetPendingPasswordChangeHeadersAsync(
        PendingPasswordChangeFilter filter,
        int startIndex,
        int count,
        string sortBy,
        bool sortDescending,
        bool includeTotalCount);

    /// <summary>
    /// What the whole queue holds, for the summary above a queue list. Counted in the database rather than from
    /// a materialised list, because the list is windowed and the summary is not.
    /// </summary>
    Task<PasswordQueueSummary> GetPasswordQueueSummaryAsync(DateTime asOf);

    /// <summary>
    /// Makes every change matching <paramref name="filter"/> due immediately, clearing the failure that stopped
    /// it, and returns how many were affected: the manual retry behind the queue page's row and bulk actions
    /// (requirement 22).
    /// <para>
    /// A delivered change cannot be retried because its row is already gone, and an expired one must not be:
    /// its password is no longer held. Both fall out of the filter naturally, the first by not existing and the
    /// second by the implementation excluding it.
    /// </para>
    /// </summary>
    Task<int> RetryPasswordChangesAsync(PendingPasswordChangeFilter filter);

    /// <summary>
    /// Records that an administrator stopped every change matching <paramref name="filter"/> being delivered,
    /// and returns how many were affected (requirement 22).
    /// <para>
    /// An outcome rather than a deletion, for the reason an expiry is one: the identity's password stays
    /// divergent on that system either way, and a row that disappears reports the opposite.
    /// </para>
    /// </summary>
    Task<int> CancelPasswordChangesAsync(
        PendingPasswordChangeFilter filter,
        Guid? cancelledById,
        string? cancelledByName,
        DateTime asOf);

    /// <summary>
    /// The distinct reasons a target gave for refusing the <see cref="PendingPasswordChangeOrigin.Provisioned"/>
    /// changes parked against a Synchronisation Rule, each with how many accounts it is holding up, most accounts
    /// first (#1697). The Synchronisation Rule surfaces' source of truth for this, now that initial passwords
    /// are delivered off this queue.
    /// </summary>
    Task<List<InitialPasswordRejection>> GetParkedProvisionedPasswordReasonsAsync(int syncRuleId);

    #endregion

    /// <summary>
    /// Bulk deletes Pending Exports.
    /// Uses raw SQL bulk operations in production for performance.
    /// </summary>
    Task DeletePendingExportsAsync(IEnumerable<PendingExport> pendingExports);

    /// <summary>
    /// Bulk updates Pending Exports.
    /// Uses raw SQL bulk operations in production for performance.
    /// </summary>
    Task UpdatePendingExportsAsync(IEnumerable<PendingExport> pendingExports);

    /// <summary>
    /// Deletes Pending Exports by their associated CSO IDs.
    /// Returns the count of deleted Pending Exports.
    /// Used during obsolete CSO processing to clean up orphaned exports, and by the MVO deletion
    /// flush's collision policy to replace a non-Delete Pending Export with a Delete one.
    /// Implementations that delete via raw SQL must also detach any tracked instances of the
    /// deleted rows (and their attribute value changes) from the change tracker, otherwise EF
    /// Core's SetNull cascade fix-up on <c>PendingExport.SourceMetaverseObjectId</c> targets a
    /// deleted row when the source Metaverse Object is deleted on the same context and throws
    /// <c>DbUpdateConcurrencyException</c>.
    /// </summary>
    Task<int> DeletePendingExportsByConnectedSystemObjectIdsAsync(IEnumerable<Guid> connectedSystemObjectIds);

    /// <summary>
    /// Deletes Connected System Objects by id, without requiring a tracked entity graph.
    /// <para>
    /// Used for a successful Delete export against a Connected System Object whose provisioning was never
    /// confirmed (Status stayed PendingProvisioning): export batches load Pending Exports
    /// <c>AsNoTracking()</c> with the Connected System Object graph included, so passing that graph to
    /// <see cref="DeleteConnectedSystemObjectsAsync"/> (which does <c>RemoveRange</c> on a tracked
    /// <c>DbSet</c>) can throw "another instance with the same key value is already being tracked".
    /// Deleting by id avoids attaching the untracked graph at all.
    /// </para>
    /// <para>
    /// Implementations must null incoming reference values from other rows before deleting (mirroring
    /// <see cref="DeleteConnectedSystemObjectsAsync"/>'s reference-clearing step), and must detach any
    /// tracked instances of the deleted rows and their attribute values from the change tracker, for the
    /// same reason documented on <see cref="DeletePendingExportsByConnectedSystemObjectIdsAsync"/>.
    /// </para>
    /// </summary>
    /// <returns>The number of Connected System Objects deleted.</returns>
    Task<int> DeleteConnectedSystemObjectsByIdsAsync(IReadOnlyCollection<Guid> connectedSystemObjectIds);

    /// <summary>
    /// Gets a single Pending Export by its associated CSO ID.
    /// Used during export evaluation to check for existing Pending Exports in the database.
    /// </summary>
    Task<PendingExport?> GetPendingExportByConnectedSystemObjectIdAsync(Guid connectedSystemObjectId);

    /// <summary>
    /// Lean fetch for the export evaluation merge-and-replace path: only AttributeValueChanges
    /// (with Attribute) are loaded, skipping ConnectedSystemObject, ConnectedSystem and
    /// SourceMetaverseObject and their attribute value graphs (issue #986). Use this instead of
    /// <see cref="GetPendingExportByConnectedSystemObjectIdAsync"/> on the merge hot path.
    /// </summary>
    Task<PendingExport?> GetPendingExportLightweightByConnectedSystemObjectIdAsync(Guid connectedSystemObjectId);

    /// <summary>
    /// Gets Pending Exports for multiple CSOs in a single lightweight query, keyed by CSO ID.
    /// Only AttributeValueChanges (with Attribute) are loaded; entities are untracked.
    /// </summary>
    Task<Dictionary<Guid, PendingExport>> GetPendingExportsLightweightByConnectedSystemObjectIdsAsync(IEnumerable<Guid> connectedSystemObjectIds);

    /// <summary>
    /// Gets CSO IDs that have Pending Exports for a Connected System.
    /// Used during import reconciliation to identify which CSOs have outstanding exports.
    /// </summary>
    Task<HashSet<Guid>> GetCsoIdsWithPendingExportsByConnectedSystemAsync(int connectedSystemId);

    /// <summary>
    /// Loads all Pending Exports for a Connected System, keyed by CSO ID. More efficient than
    /// per-page loading for large-scale reconciliation. Loads in bounded keyset-paginated chunks
    /// so each statement stays inside the database's statement timeout regardless of backlog size.
    /// </summary>
    /// <param name="connectedSystemId">The Connected System whose Pending Exports to load.</param>
    /// <param name="chunkSize">Pending Exports loaded per statement; null uses the implementation default.</param>
    Task<Dictionary<Guid, PendingExport>> GetPendingExportsLightweightByConnectedSystemIdAsync(int connectedSystemId, int? chunkSize = null);

    /// <summary>
    /// Deletes Pending Exports that are not tracked by the EF change tracker.
    /// Used during import reconciliation to clean up confirmed exports.
    /// </summary>
    Task DeleteUntrackedPendingExportsAsync(IEnumerable<PendingExport> untrackedPendingExports);

    /// <summary>
    /// Deletes Pending Export attribute value changes that are not tracked by the EF change tracker.
    /// Used during import reconciliation to clean up partially confirmed exports.
    /// </summary>
    Task DeleteUntrackedPendingExportAttributeValueChangesAsync(IEnumerable<PendingExportAttributeValueChange> untrackedAttributeValueChanges);

    /// <summary>
    /// Updates Pending Exports that are not tracked by the EF change tracker.
    /// Used during import reconciliation to update export status after confirmation.
    /// </summary>
    Task UpdateUntrackedPendingExportsAsync(IEnumerable<PendingExport> untrackedPendingExports);

    #endregion

    #region Activity and RPEIs

    /// <summary>
    /// Updates an activity's fields (progress, message, status, etc.).
    /// </summary>
    Task UpdateActivityAsync(Activity activity);

    /// <summary>
    /// Updates an activity's message field.
    /// Convenience method that updates only the Message property.
    /// </summary>
    Task UpdateActivityMessageAsync(Activity activity, string message);

    /// <summary>
    /// Updates activity progress fields using an independent database connection,
    /// bypassing any in-flight transaction on the main connection.
    /// Progress updates are immediately visible to other sessions (e.g., the UI).
    /// </summary>
    Task UpdateActivityProgressOutOfBandAsync(Activity activity);

    /// <summary>
    /// Records the given phases of a Run Profile execution (#454), inserting phases the Activity
    /// has not seen before and updating the state of ones it has. Called with the phases declared
    /// when the run starts, and thereafter with just the phases each transition changed.
    /// </summary>
    /// <remarks>
    /// Narrating a run must never fail it: callers treat a failure here as cosmetic. Writes are
    /// idempotent, so a retried call is harmless.
    /// </remarks>
    Task SaveActivityPhasesAsync(IReadOnlyList<ActivityPhase> phases);

    /// <summary>
    /// Records how many entries an import read from the Connected System and discarded because an excluded
    /// Container carved them out (#1255), keyed by that Container's id.
    /// </summary>
    /// <remarks>
    /// Counts are added to whatever the Activity already holds, so a paged import can call this once per page or
    /// once at the end and reach the same total. Callers pass only Containers that discarded something; a zero is
    /// not worth a row, and every Activity would otherwise carry one per excluded Container.
    ///
    /// Reporting how a run was performed must never fail it, so callers treat a failure here as cosmetic, exactly
    /// as they do for <see cref="SaveActivityPhasesAsync"/>.
    /// </remarks>
    Task RecordExclusionDiscardCountsAsync(Guid activityId, IReadOnlyDictionary<int, long> entriesDiscardedByContainerId);

    /// <summary>
    /// Bulk inserts RPEIs via raw SQL, bypassing the EF change tracker.
    /// Returns true if raw SQL was used (RPEIs are outside EF tracking),
    /// false if the EF fallback was used (RPEIs remain tracked).
    /// </summary>
    Task<bool> BulkInsertRpeisAsync(List<ActivityRunProfileExecutionItem> rpeis);

    /// <summary>
    /// Bulk inserts causal edges via chunked raw SQL, bypassing the EF change tracker (#1223).
    /// </summary>
    /// <remarks>
    /// Call this from inside the transaction that persists the effects the edges describe, never on its own.
    /// An edge that outlived a rolled-back flush would attribute a cause to an effect that never happened, and
    /// an effect persisted without its edges would read as uncaused; joining the existing flush means a failure
    /// here fails or retries with the RPEI batch exactly as a failure to persist the RPEIs themselves does, so
    /// provenance adds no new failure mode to synchronisation.
    /// </remarks>
    Task BulkInsertCausalEdgesAsync(List<CausalEdge> edges);

    /// <summary>
    /// Bulk updates OutcomeSummary and error fields on already-persisted RPEIs,
    /// and inserts any new SyncOutcomes added after initial persistence.
    /// Used by confirming imports to merge reconciliation outcomes onto existing RPEIs.
    /// </summary>
    Task BulkUpdateRpeiOutcomesAsync(List<ActivityRunProfileExecutionItem> rpeis, List<ActivityRunProfileExecutionItemSyncOutcome> newOutcomes);

    /// <summary>
    /// Loads persisted RPEIs (with their SyncOutcomes) and the id of each RPEI's existing
    /// MetaverseObjectChange, in a single round-trip. Used by cross-page reference resolution to
    /// merge reference Attribute Flow into existing RPEIs and to route the new attribute rows
    /// under the already-persisted MvoChange parent (respecting the unique constraint
    /// <c>IX_MetaverseObjectChanges_ActivityRunProfileExecutionItemId</c>). Previous implementations
    /// relied on <c>_activity.RunProfileExecutionItems</c> as a lookup source, but per-page raw-SQL
    /// flushes clear that collection so the lookup has to come from the database.
    ///
    /// PostgreSQL implementation uses a single <c>NpgsqlBatch</c> (RPEIs joined LEFT to
    /// <c>MetaverseObjectChanges</c> + a second statement for <c>SyncOutcomes</c>) in one network
    /// round-trip. Replaces the previous two-call pattern (EF Include for RPEIs + a separate raw-SQL
    /// map lookup for MvoChange ids).
    /// </summary>
    Task<List<CrossPageMergeRpei>> GetRpeisWithMvoChangeIdsForCrossPageMergeAsync(
        Guid activityId, IReadOnlyCollection<Guid> csoIds);

    /// <summary>
    /// Detaches RPEIs from the EF change tracker so they are not persisted by subsequent
    /// SaveChangesAsync calls. Call after raw SQL bulk insert has persisted them.
    /// </summary>
    void DetachRpeisFromChangeTracker(List<ActivityRunProfileExecutionItem> rpeis);

    /// <summary>
    /// Queries the database for RPEI error counts without loading RPEIs into memory.
    /// Returns total RPEIs with errors, total RPEIs, and total UnhandledError RPEIs.
    /// UnhandledError RPEIs indicate code/logic bugs and escalate activity status.
    /// </summary>
    Task<(int TotalWithErrors, int TotalRpeis, int TotalUnhandledErrors)> GetActivityRpeiErrorCountsAsync(Guid activityId);

    /// <summary>
    /// Persists ConnectedSystemObjectChange records attached to RPEIs.
    /// Used by the export processor to persist change history after RPEI raw SQL bulk insert
    /// (which only inserts RPEI scalar columns, not related change records).
    /// </summary>
    Task PersistRpeiCsoChangesAsync(List<ActivityRunProfileExecutionItem> rpeis);

    #endregion

    #region Synchronisation Rules and Configuration

    /// <summary>
    /// Gets Synchronisation Rules for a Connected System.
    /// When <paramref name="includeDisabled"/> is false, only active rules are returned.
    /// </summary>
    Task<List<SyncRule>> GetSyncRulesAsync(int connectedSystemId, bool includeDisabled, bool withChangeTracking = false);

    /// <summary>
    /// Gets all Synchronisation Rules across all Connected Systems.
    /// Used to build the drift detection cache which needs rules from all systems.
    /// </summary>
    Task<List<SyncRule>> GetAllSyncRulesAsync(bool withChangeTracking = false);

    /// <summary>
    /// Gets just the <c>Name</c> of every requested Synchronisation Rule, keyed by id (#1519 follow-up). An id
    /// with no corresponding row (the rule has been deleted) is simply absent from the result. Backs a
    /// change-history attribution name lookup for ids not already known in memory.
    /// </summary>
    /// <param name="syncRuleIds">The Synchronisation Rule ids to resolve. Deduplicated internally.</param>
    Task<Dictionary<int, string>> GetSyncRuleNamesByIdsAsync(IReadOnlyCollection<int> syncRuleIds);

    /// <summary>
    /// Gets the most recent configuration change instant across all Synchronisation Rules and their mappings
    /// (each entity's LastUpdated, falling back to Created), or null when no rules exist. Rule enable/disable,
    /// scoping and matching changes stamp the rule; mapping creation, attribute priority reordering and
    /// "Null is a value" changes stamp the mapping, so this covers every configuration surface that affects
    /// inbound resolution. A Full Synchronisation compares it against the last completed sync to decide whether
    /// the unchanged-object optimisation must be disabled for the run so new configuration reaches every object.
    /// </summary>
    Task<DateTime?> GetLatestSyncRuleConfigurationChangeAsync();

    /// <summary>
    /// Narrows a set of Synchronisation Rule IDs to those that ask for an initial password on the accounts
    /// they provision.
    /// <para>
    /// Asked at the moment a batch of Creates has succeeded, rather than stamped onto the export when it was
    /// staged, so that switching initial passwords on takes effect for work already queued. The alternative
    /// would silently skip every account provisioned between the export being staged and the administrator
    /// enabling the feature.
    /// </para>
    /// <para>
    /// Also what keeps the work list proportional to the deployments that use it: a system that provisions a
    /// hundred thousand accounts and asks for no passwords stages nothing at all.
    /// </para>
    /// </summary>
    Task<HashSet<int>> GetSyncRuleIdsWithInitialPasswordEnabledAsync(IReadOnlyCollection<int> syncRuleIds);

    /// <summary>
    /// Gets the object types (schema) for a Connected System.
    /// Used during sync to resolve attribute mappings.
    /// </summary>
    Task<List<ConnectedSystemObjectType>> GetObjectTypesAsync(int connectedSystemId);

    /// <summary>
    /// Updates a Connected System's fields (e.g., LastSyncCompletedAt watermark).
    /// </summary>
    Task UpdateConnectedSystemAsync(ConnectedSystem connectedSystem);

    /// <summary>
    /// Gets the id-to-display-name map of every Connected System. Used to resolve source system names
    /// for decision-time deletion policy snapshots (#119): a tiny table fetched at most once per run
    /// profile execution and cached by the caller, never per Metaverse Object.
    /// </summary>
    Task<Dictionary<int, string>> GetConnectedSystemNamesAsync();

    /// <summary>
    /// Every Metaverse Attribute id and its name, for snapshotting the relationship noun a causal edge reads
    /// back (#1223): "removed from Project Diamond's <b>Static Members</b>".
    /// </summary>
    /// <remarks>
    /// A tiny table read at most once per run profile execution and cached by the worker, in the manner of
    /// <see cref="GetConnectedSystemNamesAsync"/>. The name has to be snapshotted rather than resolved at read
    /// time because the attribute can be renamed or removed, and because the wording rule takes the
    /// relationship noun from the schema rather than from the object type.
    /// </remarks>
    Task<Dictionary<int, string>> GetMetaverseAttributeNamesAsync();

    #endregion

    #region Change Tracker Management

    /// <summary>
    /// Clears all tracked entities from the EF change tracker.
    /// Called at page boundaries to prevent memory accumulation during large sync runs.
    /// In the in-memory test implementation, this is a no-op.
    /// </summary>
    void ClearChangeTracker();

    /// <summary>
    /// Returns the number of entities currently tracked by the change tracker.
    /// Used for diagnostics to detect entity accumulation across pages.
    /// </summary>
    int GetChangeTrackerEntityCount();

    /// <summary>
    /// Selectively detaches schema/type entities from the change tracker without affecting
    /// CSOs or their AttributeValues. Prevents tracker bloat during import processing.
    /// </summary>
    void DetachSchemaEntitiesFromTracker();

    /// <summary>
    /// Controls whether SaveChangesAsync automatically calls DetectChanges.
    /// Disabled during batch page processing to prevent navigation property traversal
    /// from discovering conflicting entity instances after ClearChangeTracker.
    /// In the in-memory test implementation, this is a no-op.
    /// </summary>
    void SetAutoDetectChangesEnabled(bool enabled);

    #endregion

    #region MVO Change History

    /// <summary>
    /// Creates an MVO change record directly via raw SQL, bypassing EF tracking.
    /// Used for deletion change records where the MVO is about to be removed.
    /// </summary>
    Task CreateMetaverseObjectChangeDirectAsync(MetaverseObjectChange change);

    /// <summary>
    /// Persists pending MVO change records in a single outer transaction. Handles both sets
    /// in one round-trip so the pair is atomic and the per-flush BEGIN/COMMIT overhead is halved
    /// when the cross-page phase produces both:
    /// <list type="bullet">
    ///   <item><paramref name="newChanges"/> — new parent <c>MetaverseObjectChange</c> rows plus
    ///     their attribute and value children (written via raw SQL COPY).</item>
    ///   <item><paramref name="attributeAppendsToExistingChanges"/> — attribute and value children
    ///     appended to parents already persisted in an earlier page flush. Each change must have
    ///     its <c>Id</c> set to the existing parent's id; the parent row itself is not re-inserted
    ///     (respecting <c>IX_MetaverseObjectChanges_ActivityRunProfileExecutionItemId</c>).</item>
    /// </list>
    /// Used by sync processors to persist changes collected during page processing, bypassing the
    /// EF change tracker which is cleared at page boundaries.
    /// </summary>
    Task PersistPendingMvoChangesAsync(
        List<MetaverseObjectChange> newChanges,
        List<MetaverseObjectChange> attributeAppendsToExistingChanges);

    #endregion

    #region Connected System Object — Singular Convenience Methods

    /// <summary>
    /// Creates a single CSO. Convenience wrapper around <see cref="CreateConnectedSystemObjectsAsync(List{ConnectedSystemObject})"/>.
    /// </summary>
    Task CreateConnectedSystemObjectAsync(ConnectedSystemObject connectedSystemObject);

    /// <summary>
    /// Updates a single CSO. Convenience wrapper around <see cref="UpdateConnectedSystemObjectsAsync(List{ConnectedSystemObject})"/>.
    /// </summary>
    Task UpdateConnectedSystemObjectAsync(ConnectedSystemObject connectedSystemObject);

    /// <summary>
    /// Updates a single CSO with new attribute values (e.g., secondary external ID during export).
    /// Convenience wrapper around <see cref="UpdateConnectedSystemObjectsWithNewAttributeValuesAsync"/>.
    /// </summary>
    Task UpdateConnectedSystemObjectWithNewAttributeValuesAsync(
        ConnectedSystemObject connectedSystemObject,
        List<ConnectedSystemObjectAttributeValue> newAttributeValues);

    /// <summary>
    /// Atomically claims an unjoined Connected System Object for a Metaverse Object during export
    /// matching (join-before-provision); the claim succeeds only if the object is still unclaimed
    /// at write time, guarding against two Metaverse Objects racing to join the same object;
    /// returns true if the claim was written, false if another Metaverse Object claimed it first.
    /// On success the row's MetaverseObjectId, JoinType (Joined), DateJoined and Status (Normal)
    /// are set; the caller owns fixing up any tracked instance to match (raw SQL bypasses the
    /// change tracker).
    /// </summary>
    Task<bool> TryClaimConnectedSystemObjectForJoinAsync(Guid connectedSystemObjectId, Guid metaverseObjectId, DateTime dateJoined);

    #endregion

    #region Pending Export — Singular Convenience Methods

    /// <summary>
    /// Creates a single Pending Export. Convenience wrapper around <see cref="CreatePendingExportsAsync"/>.
    /// </summary>
    Task CreatePendingExportAsync(PendingExport pendingExport);

    /// <summary>
    /// Deletes a single Pending Export. Convenience wrapper around <see cref="DeletePendingExportsAsync"/>.
    /// </summary>
    Task DeletePendingExportAsync(PendingExport pendingExport);

    /// <summary>
    /// Updates a single Pending Export. Convenience wrapper around <see cref="UpdatePendingExportsAsync"/>.
    /// </summary>
    Task UpdatePendingExportAsync(PendingExport pendingExport);

    /// <summary>
    /// Appends newly evaluated attribute changes onto an existing Pending Export without touching its
    /// <see cref="PendingExport.ChangeType"/> or <see cref="PendingExport.Status"/>. Used when a Metaverse
    /// Object change arrives for a PendingProvisioning Connected System Object whose Create has already
    /// been sent (or auto-confirmed away) and is awaiting confirmation by import: the Create must never be
    /// deleted and replaced (that would mean sending a second Create, which most connectors reject for an
    /// object that already exists), so the change queues Pending on the same row instead, and travels as an
    /// Update once <c>SyncEngine.ReconcileCsoAgainstPendingExport</c> confirms the Create and flips the
    /// row's ChangeType (SyncEngine.Reconciliation.cs).
    /// </summary>
    /// <param name="pendingExportId">The Pending Export to append to.</param>
    /// <param name="changesToAdd">The newly evaluated attribute changes to add, already carrying their own
    /// <see cref="PendingExportAttributeValueChange.Id"/>; implementations set
    /// <see cref="PendingExportAttributeValueChange.PendingExportId"/>.</param>
    /// <param name="changeIdsToRemove">The ids of existing attribute changes the new ones supersede (same
    /// value-level merge semantics as <c>SyncEngine.MergeAttributeChangesIntoPendingExport</c>; computed by
    /// the caller, which owns the merge policy), removed before the additions above.</param>
    Task AppendAttributeChangesToPendingExportAsync(
        Guid pendingExportId,
        IReadOnlyList<PendingExportAttributeValueChange> changesToAdd,
        IReadOnlyList<Guid> changeIdsToRemove);

    #endregion

    #region Export Evaluation Support

    /// <summary>
    /// Gets all CSOs joined to a specific MVO across all Connected Systems.
    /// Used during MVO deletion to find all provisioned CSOs for delete exports.
    /// </summary>
    Task<List<ConnectedSystemObject>> GetConnectedSystemObjectsByMetaverseObjectIdAsync(Guid metaverseObjectId);

    /// <summary>
    /// Gets all CSOs joined to any of the given MVOs across all Connected Systems, in one query,
    /// grouped by MVO ID. LEAN SHAPE: only the external ID and secondary external ID attribute
    /// values (with their Attribute) are loaded, because MVO deletion and reference recall need
    /// nothing else from the attribute graph; a full include would materialise every membership
    /// row of any deleted group (issue #993). Do not use where the full attribute graph is needed.
    /// </summary>
    Task<Dictionary<Guid, List<ConnectedSystemObject>>> GetConnectedSystemObjectsForMvoDeletionAsync(
        IReadOnlyCollection<Guid> metaverseObjectIds);

    /// <summary>
    /// Disconnects the given CSOs from their MVOs in one set-based statement: nulls
    /// <c>MetaverseObjectId</c> and <c>DateJoined</c> and resets <c>JoinType</c> to
    /// <c>NotJoined</c>. Any tracked instances are fixed up to match so a later SaveChanges
    /// does not write stale join state back. Used by MVO deletion to detach CSOs without a
    /// round trip per object (issue #993).
    /// </summary>
    Task DisconnectConnectedSystemObjectsAsync(IReadOnlyCollection<Guid> connectedSystemObjectIds);

    /// <summary>
    /// Gets CSOs joined to MVOs that are targeted by the specified Connected Systems.
    /// Returns a dictionary keyed by (MvoId, ConnectedSystemId) for O(1) lookup during export evaluation.
    /// </summary>
    Task<Dictionary<(Guid MvoId, int ConnectedSystemId), ConnectedSystemObject>> GetConnectedSystemObjectsByTargetSystemsAsync(
        IEnumerable<int> targetConnectedSystemIds);

    /// <summary>
    /// Gets CSOs joined to specific MVOs within the specified target Connected Systems.
    /// Used for per-page export evaluation cache refresh — loads only CSOs relevant to the current page's MVOs.
    /// Returns a dictionary keyed by (MvoId, ConnectedSystemId) for O(1) lookup.
    /// </summary>
    Task<Dictionary<(Guid MvoId, int ConnectedSystemId), ConnectedSystemObject>> GetConnectedSystemObjectsByMvoIdsAndTargetSystemsAsync(
        IEnumerable<Guid> mvoIds, IEnumerable<int> targetConnectedSystemIds);

    /// <summary>
    /// Batch loads CSO attribute values for the specified CSO IDs.
    /// Used to pre-load target CSO attribute values for no-net-change detection during export evaluation.
    /// </summary>
    Task<List<ConnectedSystemObjectAttributeValue>> GetCsoAttributeValuesByCsoIdsAsync(IEnumerable<Guid> csoIds);

    /// <summary>
    /// Gets a single CSO joined to a specific MVO within a Connected System.
    /// Used during export evaluation to find existing CSOs for provisioning decisions.
    /// </summary>
    Task<ConnectedSystemObject?> GetConnectedSystemObjectByMetaverseObjectIdAsync(Guid metaverseObjectId, int connectedSystemId);

    /// <summary>
    /// Gets CSOs joined to multiple MVOs within a Connected System.
    /// Returns a dictionary keyed by MVO ID for O(1) lookup.
    /// Used for reference resolution during export processing.
    /// </summary>
    Task<Dictionary<Guid, ConnectedSystemObject>> GetConnectedSystemObjectsByMetaverseObjectIdsAsync(
        IEnumerable<Guid> metaverseObjectIds, int connectedSystemId);

    /// <summary>
    /// Gets a single Connected System Object Type attribute by ID.
    /// Used during export to determine attribute data types.
    /// </summary>
    Task<ConnectedSystemObjectTypeAttribute?> GetAttributeAsync(int id);

    /// <summary>
    /// Gets multiple Connected System Object Type attributes by their IDs.
    /// Returns a dictionary keyed by attribute ID for O(1) lookup.
    /// Used during batch export processing to pre-fetch attribute definitions.
    /// </summary>
    Task<Dictionary<int, ConnectedSystemObjectTypeAttribute>> GetAttributesByIdsAsync(IEnumerable<int> ids);

    #endregion

    #region Export Execution Support

    /// <summary>
    /// Gets the count of Pending Exports that are ready for execution.
    /// Applies database-level filtering for status, retry timing, and max retries.
    /// </summary>
    Task<int> GetExecutableExportCountAsync(int connectedSystemId);

    /// <summary>
    /// Run Profile Safeguards (#1618): the count of executable Pending Exports per change type,
    /// same filtering as <see cref="GetExecutableExportCountAsync"/>. Read once at the start of an
    /// Export run so the ledger can decide, per type, whether the whole type is withheld this run.
    /// A change type with nothing pending is absent from the dictionary.
    /// </summary>
    Task<Dictionary<PendingExportChangeType, int>> GetExecutableExportCountsByChangeTypeAsync(int connectedSystemId);

    /// <summary>
    /// Gets all Pending Exports that are ready for execution.
    /// Applies database-level filtering for status, retry timing, and max retries.
    /// </summary>
    Task<List<PendingExport>> GetExecutableExportsAsync(int connectedSystemId);

    /// <summary>
    /// Gets a batch of executable exports using keyset pagination ordered by (CreatedAt, Id).
    /// Pass the CreatedAt and Id of the last row of the previous batch to fetch the next one;
    /// pass null for both to start from the beginning. Keyset (rather than offset) paging keeps
    /// batch collection a single forward sweep even as executed rows drop out of the query and
    /// deferred rows remain in it (issue #985). Uses AsNoTracking in production for minimal EF
    /// overhead.
    /// </summary>
    /// <param name="excludedChangeTypes">Run Profile Safeguards (#1618): change types withheld for
    /// the whole run, excluded at the database level; null or empty excludes nothing.</param>
    Task<List<PendingExport>> GetExecutableExportBatchAsync(int connectedSystemId, int take, DateTime? afterCreatedAt, Guid? afterId,
        IReadOnlyCollection<PendingExportChangeType>? excludedChangeTypes = null);

    /// <summary>
    /// Collects all remaining executable exports with unresolved references (deferred) strictly
    /// after the given keyset cursor, in a single call. Used by the export batch-collection loop
    /// to fast-path once a batch is discovered to be made up entirely of deferred exports, instead
    /// of continuing to page through the remainder 100 rows at a time purely to build the deferred
    /// list (issue #985). Same ordering and filtering semantics as
    /// <see cref="GetExecutableExportBatchAsync"/>, restricted to HasUnresolvedReferences exports.
    /// </summary>
    Task<List<PendingExport>> GetRemainingDeferredExportsAsync(int connectedSystemId, DateTime? afterCreatedAt, Guid? afterId);

    /// <summary>
    /// Returns whether any executable exports WITHOUT unresolved references exist strictly after
    /// the given keyset cursor. Guards the deferred-collection fast path (issue #985): deferred
    /// and executable exports interleave in (CreatedAt, Id) order, so an all-deferred batch does
    /// not prove the rest of the queue is deferred too. Same filtering semantics as
    /// <see cref="GetExecutableExportBatchAsync"/>, restricted to non-deferred exports; a cheap
    /// existence check with no entity materialisation.
    /// </summary>
    Task<bool> AnyExecutableNonDeferredExportsAfterAsync(int connectedSystemId, DateTime? afterCreatedAt, Guid? afterId);

    /// <summary>
    /// Marks Pending Exports as Executing with the current UTC timestamp.
    /// Uses raw SQL in production for efficiency.
    /// </summary>
    Task MarkPendingExportsAsExecutingAsync(IList<PendingExport> pendingExports);

    /// <summary>
    /// Reloads Pending Exports by their IDs with full object graph.
    /// Used during parallel export processing to reload exports in a separate context.
    /// </summary>
    Task<List<PendingExport>> GetPendingExportsByIdsAsync(IList<Guid> pendingExportIds);

    #endregion

    #region Preview Backstops (#288)

    /// <summary>
    /// Begins a database transaction that is unconditionally rolled back when the returned scope is disposed,
    /// whatever happened inside it: the outermost defence-in-depth layer around the synchronisation preview's
    /// zero-side-effect guarantee (PRD requirement 8). Any write that slipped past the preview path's other
    /// guards is discarded rather than committed. Returns null when the underlying provider is not relational
    /// (the in-memory test repository), where there is no transaction to hold; the preview's other layers
    /// still apply there.
    /// </summary>
    Task<IAsyncDisposable?> BeginRollbackOnlyTransactionAsync();

    #endregion

    #region Generated Values (#242)

    /// <summary>
    /// Which of the given normalised (lower-cased) values some Metaverse Object other than
    /// <paramref name="excludingMetaverseObjectId"/> already holds for <paramref name="metaverseAttributeId"/>:
    /// the Metaverse availability gate (Unique Value Generation, plan "The service"). Id-only: no
    /// <see cref="MetaverseObject"/> is hydrated. Case-insensitive over the <c>LOWER("StringValue")</c>
    /// expression index (plan decision 13); callers must pass values already lower-cased so the comparison
    /// matches the index expression exactly rather than relying on the database to lower-case again. Returns an
    /// empty set for an empty <paramref name="normalisedValues"/> without querying.
    /// </summary>
    Task<HashSet<string>> GetMetaverseAttributeValuesInUseAsync(int metaverseAttributeId, IReadOnlyCollection<string> normalisedValues, Guid? excludingMetaverseObjectId);

    /// <summary>
    /// The connector-space counterpart of <see cref="GetMetaverseAttributeValuesInUseAsync"/>, over
    /// <c>ConnectedSystemObjectAttributeValues</c>. A Connected System Object Type attribute id is unique across
    /// every Connected System, so no system id is needed to disambiguate which system's attribute this is.
    /// </summary>
    Task<HashSet<string>> GetConnectedSystemAttributeValuesInUseAsync(int connectedSystemObjectTypeAttributeId, IReadOnlyCollection<string> normalisedValues, Guid? excludingConnectedSystemObjectId);

    /// <summary>
    /// The numeric counterpart of <see cref="GetMetaverseAttributeValuesInUseAsync"/>, for Number and Long
    /// Number generation targets. A candidate is taken if it matches an existing value in either <c>IntValue</c>
    /// or <c>LongValue</c>: the two columns back the same attribute type distinction, not two independent value
    /// spaces, so a number already held as one still collides with the other.
    /// </summary>
    Task<HashSet<long>> GetMetaverseAttributeNumbersInUseAsync(int metaverseAttributeId, IReadOnlyCollection<long> values, Guid? excludingMetaverseObjectId);

    /// <summary>
    /// The connector-space counterpart of <see cref="GetMetaverseAttributeNumbersInUseAsync"/>.
    /// </summary>
    Task<HashSet<long>> GetConnectedSystemAttributeNumbersInUseAsync(int connectedSystemObjectTypeAttributeId, IReadOnlyCollection<long> values, Guid? excludingConnectedSystemObjectId);

    /// <summary>
    /// Which of the given normalised (lower-cased) values a live <see cref="GeneratedValueAssignment"/> already
    /// holds for the given attribute: the fifth gate ("other objects' live assignments for the attribute", plan
    /// "The service") and the adopt-before-generate conflict check, both targeted reads over the filtered unique
    /// indexes (<c>IX_GeneratedValueAssignments_MvAttributeId_NormalisedValue_Unique</c> and its Connected
    /// System counterpart) rather than a scan of every assignment a generation has ever produced. Exactly one
    /// of <paramref name="metaverseAttributeId"/> and <paramref name="connectedSystemObjectTypeAttributeId"/>
    /// must be given (both set or neither set throws <see cref="ArgumentException"/>), matching the attribute
    /// the caller's mode targets, never the <c>SyncRuleMappingGeneration</c> that produced the request: two
    /// different generation rows targeting the same attribute (plan decision 3) must not be able to issue the
    /// same value to two different objects, which scoping this by generation instead of by attribute would miss.
    /// A row whose object (the <c>MetaverseObjectId</c> or <c>ConnectedSystemObjectId</c> for the mode) is
    /// <paramref name="excludingObjectId"/> is not counted as held, the same self-exclusion every other gate
    /// gives the requesting object. Callers must pass values already lower-cased, matching every other gate's
    /// convention. Returns an empty set for an empty <paramref name="normalisedValues"/> without querying.
    /// </summary>
    Task<HashSet<string>> GetGeneratedValueAssignmentValuesInUseAsync(int? metaverseAttributeId, int? connectedSystemObjectTypeAttributeId, IReadOnlyCollection<string> normalisedValues, Guid? excludingObjectId);

    /// <summary>
    /// Creates one or more <see cref="GeneratedValueAssignment"/> rows. A concurrent insert that collides on the
    /// cross-assignment unique index on (attribute, normalised value) (plan decision 13) surfaces as
    /// <see cref="JIM.Models.Exceptions.GeneratedValueConflictException"/>, so the losing side of the race can
    /// recognise it and draw the next candidate, rather than the write failing as an unclassified database error.
    /// </summary>
    Task CreateGeneratedValueAssignmentsAsync(IReadOnlyCollection<GeneratedValueAssignment> assignments);

    /// <summary>
    /// Saves changes to an existing, already-persisted assignment, stamping
    /// <see cref="GeneratedValueAssignment.LastUpdated"/>.
    /// </summary>
    Task UpdateGeneratedValueAssignmentAsync(GeneratedValueAssignment assignment);

    /// <summary>
    /// Deletes assignments by id: page-flush lifecycle reconciliation's removal when another contributor wins an
    /// attribute or a value is recalled (plan: Assignment lifecycle). Retirement, where it applies, is the
    /// caller's responsibility to write first; deleting the assignment here does not retire its value.
    /// </summary>
    Task DeleteGeneratedValueAssignmentsAsync(IReadOnlyCollection<Guid> assignmentIds);

    /// <summary>
    /// The live assignment for a Metaverse Object's generated attribute (import mode), if any.
    /// </summary>
    Task<GeneratedValueAssignment?> GetGeneratedValueAssignmentAsync(Guid metaverseObjectId, int metaverseAttributeId);

    /// <summary>
    /// The export-mode counterpart of <see cref="GetGeneratedValueAssignmentAsync"/>: the live assignment for a
    /// Connected System Object's generated attribute, if any.
    /// </summary>
    Task<GeneratedValueAssignment?> GetGeneratedValueAssignmentForConnectedSystemObjectAsync(Guid connectedSystemObjectId, int connectedSystemObjectTypeAttributeId);

    /// <summary>
    /// Every live assignment (import mode) for the given Metaverse Objects, across every generated attribute.
    /// The caller batches per page; this loads exactly the ids it is given. <c>AsNoTracking</c>.
    /// </summary>
    Task<List<GeneratedValueAssignment>> GetGeneratedValueAssignmentsForMetaverseObjectsAsync(IReadOnlyCollection<Guid> metaverseObjectIds);

    /// <summary>
    /// The export-mode counterpart of <see cref="GetGeneratedValueAssignmentsForMetaverseObjectsAsync"/>, keyed
    /// on Connected System Object.
    /// </summary>
    Task<List<GeneratedValueAssignment>> GetGeneratedValueAssignmentsForConnectedSystemObjectsAsync(IReadOnlyCollection<Guid> connectedSystemObjectIds);

    /// <summary>
    /// Every live assignment a generated mapping is currently responsible for, across every object it has
    /// produced a value for. Used when the mapping's settings change in a way that must revisit its assignments.
    /// </summary>
    Task<List<GeneratedValueAssignment>> GetGeneratedValueAssignmentsForGenerationAsync(int syncRuleMappingGenerationId);

    /// <summary>
    /// The sequence counter for a target attribute, if one has been created yet (the first block reservation
    /// creates it; see <see cref="ReserveGeneratedValueSequenceBlockAsync"/>). Exactly one of
    /// <paramref name="metaverseAttributeId"/> and <paramref name="connectedSystemObjectTypeAttributeId"/> must
    /// be given; both set or neither set throws <see cref="ArgumentException"/>.
    /// </summary>
    Task<GeneratedValueSequence?> GetGeneratedValueSequenceAsync(int? metaverseAttributeId, int? connectedSystemObjectTypeAttributeId);

    /// <summary>
    /// The seed for a counter's first use (plan decision 3): the highest existing value already held for the
    /// attribute, read across numeric storage (<c>IntValue</c>, <c>LongValue</c>) and purely-numeric text values
    /// (<c>StringValue</c> matching <c>^[0-9]+$</c>, between 1 and 18 digits so it fits a <c>bigint</c>, cast to
    /// <c>bigint</c>). Null when the attribute holds no such value anywhere.
    /// <para>
    /// A prefixed or zero-padded text value (for example "EMP0042") is not purely numeric, so it is never
    /// considered here; a sequence flow that expects to pick up numbering from values shaped like that will not
    /// see them and starts from its own configured start value instead.
    /// </para>
    /// </summary>
    Task<long?> GetHighestNumericValueForAttributeAsync(int? metaverseAttributeId, int? connectedSystemObjectTypeAttributeId);

    /// <summary>
    /// Atomically reserves a block of <paramref name="count"/> numbers from the attribute's counter and returns
    /// the first number of the block; the reserved block is
    /// <c>first, first + increment, ..., first + (count - 1) * increment</c>. Creates the counter row, seeded at
    /// <paramref name="floor"/>, the first time this attribute is reserved against; a concurrent creation that
    /// loses the race is a harmless no-op, since the advance that follows is what actually moves the counter.
    /// <para>
    /// The counter only ever moves forward: its stored value becomes
    /// <c>GREATEST(current, floor) + count * increment</c>, so a <paramref name="floor"/> below the counter's
    /// current position has no effect beyond this call's own advance, and two concurrent reservations against the
    /// same attribute serialise on the row and never overlap.
    /// </para>
    /// <para>
    /// Any numbers in the reserved block the caller ultimately does not issue (for example because the object
    /// generation failed after the block was reserved) are simply never used; the resulting gap in the sequence
    /// is expected (plan decision 3), is never backfilled, and never causes a number to be re-issued.
    /// </para>
    /// </summary>
    Task<long> ReserveGeneratedValueSequenceBlockAsync(int? metaverseAttributeId, int? connectedSystemObjectTypeAttributeId, long floor, int count, int increment);

    /// <summary>
    /// Advances a counter's display-only <see cref="GeneratedValueSequence.AssignedCount"/> by
    /// <paramref name="by"/>, once the caller has determined how many numbers from a reserved block were
    /// actually issued. Never gates anything; it exists purely to keep the counter's "issued so far" figure
    /// accurate for display.
    /// </summary>
    Task IncrementGeneratedValueSequenceAssignedCountAsync(int sequenceId, long by);

    /// <summary>
    /// Raises a counter's <see cref="GeneratedValueSequence.NextValue"/> to <paramref name="newStart"/> when
    /// that is higher than its current position, stamping <see cref="GeneratedValueSequence.LastMovedAt"/> and
    /// <see cref="GeneratedValueSequence.LastMovedBySyncRuleMappingId"/> (Unique Value Generation, #242, plan
    /// decision 3: a generated Sequence mapping's save reports the skip when its configured
    /// <see cref="Logic.SyncRuleMappingGeneration.SequenceStart"/> is raised above the counter). Never seeds a
    /// counter that does not exist yet: with nothing to move, there is nothing to report, and the correct seed
    /// for a first-ever use is decided at generation time (the higher of the flow's start and the attribute's
    /// highest existing value), which this method deliberately leaves alone.
    /// </summary>
    /// <returns>
    /// The counter's <see cref="GeneratedValueSequence.NextValue"/> before the raise, when a row existed and
    /// <paramref name="newStart"/> raised it; null when no counter row exists yet, or one exists but
    /// <paramref name="newStart"/> is at or below its current position (no effect either way).
    /// </returns>
    Task<long?> RaiseGeneratedValueSequenceIfHigherAsync(int? metaverseAttributeId, int? connectedSystemObjectTypeAttributeId, long newStart, int syncRuleMappingId);

    /// <summary>
    /// Unconditionally sets a counter's <see cref="GeneratedValueSequence.NextValue"/> to <paramref name="newValue"/>,
    /// in either direction, stamping <see cref="GeneratedValueSequence.LastMovedAt"/> and
    /// <see cref="GeneratedValueSequence.LastMovedBySyncRuleMappingId"/>: "Start again" (plan "The service",
    /// <c>StartAgainAsync</c>), which deliberately moves the counter backwards to the flow's configured start
    /// value. A no-op, reported as no change, when no counter row exists yet for the attribute; "Start again"
    /// has nothing to restart until the counter has been seeded by a real generation.
    /// </summary>
    /// <returns>The counter's <see cref="GeneratedValueSequence.NextValue"/> before the move, or null when no
    /// counter row exists yet.</returns>
    Task<long?> ResetGeneratedValueSequenceAsync(int? metaverseAttributeId, int? connectedSystemObjectTypeAttributeId, long newValue, int syncRuleMappingId);

    /// <summary>
    /// How many Metaverse Objects of <paramref name="metaverseObjectTypeId"/>, joined to a Connected System
    /// Object of <paramref name="connectedSystemId"/>, currently hold no value for <paramref name="metaverseAttributeId"/>
    /// (Unique Value Generation, #242, Phase 3): the count behind a generated import mapping's "N existing
    /// objects would receive a value on the next full synchronisation" preview line. Works against a mapping
    /// that has not yet been saved (the ids are supplied directly, not resolved from a persisted mapping), so
    /// the portal can show this while an administrator is still composing the mapping.
    /// </summary>
    Task<int> CountMetaverseObjectsAwaitingGeneratedValueAsync(int metaverseObjectTypeId, int connectedSystemId, int metaverseAttributeId);

    /// <summary>
    /// The committed generated values <paramref name="metaverseObjectId"/> currently holds, one row per live
    /// import-mode assignment, denormalised with the attribute, Synchronisation Rule and mapping names a display
    /// surface needs (Unique Value Generation, #242, Phase 3). An EF projection (a UI read, not a worker hot
    /// path); empty when the object holds no generated values.
    /// </summary>
    Task<List<GeneratedValueAssignmentHeader>> GetGeneratedValueAssignmentHeadersForMetaverseObjectAsync(Guid metaverseObjectId);

    #endregion
}
