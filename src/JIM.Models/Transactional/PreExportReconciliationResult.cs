namespace JIM.Models.Transactional;

/// <summary>
/// Result of pre-export CREATE→DELETE reconciliation.
/// Identifies pending export pairs that cancel each other out and should not be exported.
/// </summary>
public class PreExportReconciliationResult
{
    /// <summary>
    /// Pairs of pending exports that were reconciled (CREATE+DELETE or UPDATE+DELETE for the same CSO).
    /// </summary>
    public List<ReconciledExportPair> ReconciledPairs { get; } = [];

    /// <summary>
    /// Total number of pending exports cancelled by reconciliation.
    /// </summary>
    public int TotalCancelled => ReconciledPairs.Sum(p => p.CancelledExportIds.Count);
}

/// <summary>
/// Represents a reconciled pair of pending exports targeting the same Connected System Object.
/// </summary>
public class ReconciledExportPair
{
    /// <summary>
    /// The Connected System Object ID that both exports targeted.
    /// </summary>
    public Guid? ConnectedSystemObjectId { get; set; }

    /// <summary>
    /// The Metaverse Object that triggered the exports.
    /// </summary>
    public Guid? SourceMetaverseObjectId { get; set; }

    /// <summary>
    /// IDs of the pending exports that should be cancelled (removed from persistence).
    /// For CREATE+DELETE(Pending): both IDs. For UPDATE+DELETE: only the UPDATE ID.
    /// </summary>
    public List<Guid> CancelledExportIds { get; } = [];

    /// <summary>
    /// Human-readable reason for the reconciliation.
    /// </summary>
    public string Reason { get; set; } = string.Empty;
}
