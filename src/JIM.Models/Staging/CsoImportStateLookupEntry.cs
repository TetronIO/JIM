// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// SPEC-1082 D8: one entry in the pre-fetched Full Import lookup dictionary, keyed by the same
/// composite external-id string as <c>ISyncRepository.GetAllCsoExternalIdMappingsAsync</c>. A
/// value-type record struct so the ~500k-entry dictionary used at scale adds no per-row heap
/// allocation beyond the existing string keys (D13).
/// </summary>
/// <param name="CsoId">The matched Connected System Object's ID.</param>
/// <param name="ImportStateHash">The stored content hash from the last stamp, or null if never stamped.</param>
/// <param name="ImportStateFingerprint">The stored schema fingerprint from the last stamp, or null if never stamped.</param>
/// <param name="Status">The CSO's current lifecycle status; only <see cref="ConnectedSystemObjectStatus.Normal"/> is skip-eligible.</param>
/// <param name="PartitionId">The CSO's current partition, used to detect a pending partition backfill that would disqualify a skip.</param>
public readonly record struct CsoImportStateLookupEntry(
    Guid CsoId,
    Guid? ImportStateHash,
    Guid? ImportStateFingerprint,
    ConnectedSystemObjectStatus Status,
    int? PartitionId);
