using JIM.Models.Staging;

namespace JIM.Models.Transactional;

/// <summary>
/// Result of export evaluation including pending exports and no-net-change statistics.
/// </summary>
public class ExportEvaluationResult
{
    /// <summary>
    /// List of PendingExports that were created.
    /// </summary>
    public List<PendingExport> PendingExports { get; set; } = [];

    /// <summary>
    /// List of CSOs created for provisioning (when deferSave is true).
    /// These need to be batch-persisted by the caller before the pending exports.
    /// </summary>
    public List<ConnectedSystemObject> ProvisioningCsosToCreate { get; set; } = [];

    /// <summary>
    /// Count of attributes skipped because the CSO already has the current value.
    /// This represents true no-net-changes where the MVO had updates but the CSO matches.
    /// </summary>
    public int CsoAlreadyCurrentCount { get; set; }
}
