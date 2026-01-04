namespace JIM.Models.Enums;

/// <summary>
/// Indicates why a no-net-change was detected during export evaluation.
/// Used when ObjectChangeType is NoChange.
/// </summary>
public enum NoChangeReason
{
    /// <summary>
    /// Default value, not set.
    /// </summary>
    NotSet,

    /// <summary>
    /// No MVO attributes relevant to the export rule have changed.
    /// The export evaluation was skipped because there were no attribute changes to evaluate.
    /// </summary>
    MvoNoAttributeChanges,

    /// <summary>
    /// The MVO attribute changed, but the CSO already has the same value.
    /// The pending export was not created because it would result in no net change.
    /// </summary>
    CsoAlreadyCurrent
}
