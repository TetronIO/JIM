// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Sync;

namespace JIM.Models.Transactional;

/// <summary>
/// Result of drift detection for a single CSO.
/// </summary>
public class DriftDetectionResult
{
    /// <summary>
    /// Attributes that were detected as drifted and need correction.
    /// </summary>
    public List<DriftedAttribute> DriftedAttributes { get; } = [];

    /// <summary>
    /// Whether any drift was detected.
    /// </summary>
    public bool HasDrift => DriftedAttributes.Count > 0;

    /// <summary>
    /// Pending Exports that were created to correct the drift.
    /// </summary>
    public List<PendingExport> CorrectiveExports { get; } = [];

    /// <summary>
    /// Mapping-level errors found while working out what the Connected System Object should hold (#2180): the same
    /// errors export staging records for the same mapping, such as a Missing Input Behaviour of Fail this mapping, an
    /// Expression result the target attribute cannot hold, or several source values for a single-valued target. No
    /// correction is staged for an attribute with an error; the caller records each one on a Run Profile Execution Item.
    /// </summary>
    public List<AttributeFlowError> AttributeFlowErrors { get; } = [];
}

/// <summary>
/// Represents a single attribute that has drifted from expected state.
/// </summary>
public class DriftedAttribute
{
    /// <summary>
    /// The CSO attribute that drifted.
    /// </summary>
    public required ConnectedSystemObjectTypeAttribute Attribute { get; init; }

    /// <summary>
    /// The actual value found in the CSO (may be null for missing attributes).
    /// </summary>
    public object? ActualValue { get; init; }

    /// <summary>
    /// The expected value based on MVO and export rule mapping.
    /// </summary>
    public object? ExpectedValue { get; init; }

    /// <summary>
    /// The export rule that defines the expected value.
    /// </summary>
    public required SyncRule ExportRule { get; init; }

    /// <summary>
    /// The changes that correct this attribute, when they were staged by export staging's own code rather than
    /// derived from <see cref="ExpectedValue"/> and <see cref="ActualValue"/>: an expression mapping's (#2180), so its
    /// correction is exactly what export staging would stage. Null for an ordinary mapping.
    /// </summary>
    public IReadOnlyList<PendingExportAttributeValueChange>? CorrectiveChanges { get; init; }
}
