// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional;

/// <summary>
/// An export batch as it was marked Executing (#2083, #2197): each Pending Export's retry state from just before the mark,
/// and the time the mark wrote to <see cref="PendingExport.LastAttemptedAt"/>. The time is what tells this run's mark from
/// a later run's: putting the batch back writes only rows still carrying it.
/// </summary>
/// <param name="StatesBefore">Each Pending Export's retry state just before the mark, by id.</param>
/// <param name="MarkedAt">The <see cref="PendingExport.LastAttemptedAt"/> the mark wrote.</param>
public sealed record ExecutingBatchMark(IReadOnlyDictionary<Guid, PendingExportRetryState> StatesBefore, DateTime MarkedAt);
