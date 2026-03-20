using JIM.Models.Logic;
using JIM.Models.Staging;

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
    /// Pending exports that were created to correct the drift.
    /// </summary>
    public List<PendingExport> CorrectiveExports { get; } = [];
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
}
