// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
namespace JIM.Models.Tasking;

/// <summary>
/// The data-removal half of the schema refresh decision's "Apply and Remove" option (#1485). By the time this
/// task is queued, the refreshed schema has been recorded and the invalidated Synchronisation Rules and
/// mappings deleted; this task removes the dependent data at whatever scale the Connected System holds:
/// Connected System Objects of the removed Object Types are marked Obsolete (flowing through disconnection,
/// attribute recall, grace periods and Metaverse Deletion Rules on the next synchronisation run), and stored
/// values of the removed attributes are deleted. The ids are the pre-refresh ids captured on the refresh's
/// preview; the schema rows themselves are retained (see issue #782), so the ids stay resolvable.
/// </summary>
public class SchemaRefreshRemovalWorkerTask : WorkerTask
{
    /// <summary>
    /// The id of the Connected System whose schema refresh this removal completes.
    /// </summary>
    public int ConnectedSystemId { get; set; }

    /// <summary>
    /// The pre-refresh ids of the Object Types the Connected System no longer reports. Every Connected System
    /// Object of these types is marked Obsolete.
    /// </summary>
    public List<int> RemovedObjectTypeIds { get; set; } = new();

    /// <summary>
    /// The pre-refresh ids of the attributes the Connected System no longer reports on surviving Object Types.
    /// Every stored value of these attributes is deleted. Attributes of a wholly removed Object Type are not
    /// listed here; their values leave with their objects through the obsoletion pipeline.
    /// </summary>
    public List<int> RemovedAttributeIds { get; set; } = new();

    public SchemaRefreshRemovalWorkerTask()
    {
        // for use by EntityFramework to construct db-sourced objects.
    }

    public SchemaRefreshRemovalWorkerTask(int connectedSystemId, List<int> removedObjectTypeIds, List<int> removedAttributeIds)
    {
        ConnectedSystemId = connectedSystemId;
        RemovedObjectTypeIds = removedObjectTypeIds;
        RemovedAttributeIds = removedAttributeIds;
    }

    /// <summary>
    /// Factory method for creating a task triggered by a user.
    /// </summary>
    public static SchemaRefreshRemovalWorkerTask ForUser(int connectedSystemId, List<int> removedObjectTypeIds, List<int> removedAttributeIds, Guid userId, string userName)
    {
        return new SchemaRefreshRemovalWorkerTask(connectedSystemId, removedObjectTypeIds, removedAttributeIds)
        {
            InitiatedByType = ActivityInitiatorType.User,
            InitiatedById = userId,
            InitiatedByName = userName
        };
    }

    /// <summary>
    /// Factory method for creating a task triggered by an API key.
    /// </summary>
    public static SchemaRefreshRemovalWorkerTask ForApiKey(int connectedSystemId, List<int> removedObjectTypeIds, List<int> removedAttributeIds, Guid apiKeyId, string apiKeyName)
    {
        return new SchemaRefreshRemovalWorkerTask(connectedSystemId, removedObjectTypeIds, removedAttributeIds)
        {
            InitiatedByType = ActivityInitiatorType.ApiKey,
            InitiatedById = apiKeyId,
            InitiatedByName = apiKeyName
        };
    }
}
