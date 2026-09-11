// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.PostgresData.Repositories;

/// <summary>
/// The single source of truth for the column lists used by the raw-SQL upsert that records service heartbeats
/// (SystemRepository.UpsertServiceHeartbeatAsync). The writer MUST write values in exactly list order.
/// BulkInsertColumnCompletenessTests asserts the insert list matches the EF model's mapped columns exactly, and
/// that every column has a conscious home in the update list or the documented exclusion list, so a migration
/// cannot silently leave the writer behind.
/// </summary>
internal static class ServiceHeartbeatBulkColumns
{
    /// <summary>
    /// Insert columns for the ServiceHeartbeats table. Id is omitted: it is an identity column the database assigns.
    /// </summary>
    internal static readonly string[] ServiceHeartbeats =
    [
        "Service", "InstanceId", "HostName", "Version", "StartedAt", "LastSeenAt", "CurrentWork",
        "CurrentWorkStartedAt", "LastProgressAt", "Detail"
    ];

    /// <summary>
    /// Update columns for the upsert: everything a later heartbeat from the same instance may change. HostName,
    /// Version and StartedAt cannot change within one process, but rewriting them costs nothing and means a row is
    /// always a faithful copy of the last write rather than of the first.
    /// </summary>
    internal static readonly string[] ServiceHeartbeatsUpdate =
    [
        "HostName", "Version", "StartedAt", "LastSeenAt", "CurrentWork", "CurrentWorkStartedAt", "LastProgressAt",
        "Detail"
    ];

    /// <summary>
    /// Columns deliberately excluded from the update list: the pair the upsert conflicts on. A write that changed
    /// either would be describing a different instance rather than updating this one.
    /// </summary>
    internal static readonly string[] ServiceHeartbeatsUpdateExclusions =
    [
        "Service", "InstanceId"
    ];
}
