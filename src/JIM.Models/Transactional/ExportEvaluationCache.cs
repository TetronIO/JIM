using JIM.Models.Logic;
using JIM.Models.Staging;

namespace JIM.Models.Transactional;

/// <summary>
/// Cache class for pre-loaded export evaluation data.
/// Pass this to the optimised evaluation methods to avoid O(N×M) database queries.
///
/// The cache has two tiers:
/// - Stable (rules + target system IDs): built once at sync start, reused across all pages
/// - Per-page (CsoLookup + CsoAttributeValues): rebuilt each page for only the MVOs being evaluated
/// </summary>
public class ExportEvaluationCache
{
    /// <summary>
    /// Pre-loaded export rules, keyed by MVO type ID. Stable across pages.
    /// </summary>
    public Dictionary<int, List<SyncRule>> ExportRulesByMvoTypeId { get; }

    /// <summary>
    /// Target connected system IDs (excluding source system). Stable across pages.
    /// Used to scope per-page CSO queries to only the relevant target systems.
    /// </summary>
    public IReadOnlyList<int> TargetSystemIds { get; }

    /// <summary>
    /// Per-page CSO lookup, keyed by (MvoId, ConnectedSystemId).
    /// Rebuilt each page via RefreshForPageAsync for only the MVOs being evaluated.
    /// </summary>
    public Dictionary<(Guid MvoId, int ConnectedSystemId), ConnectedSystemObject> CsoLookup { get; set; }

    /// <summary>
    /// Per-page target CSO attribute values for no-net-change detection during export evaluation.
    /// Uses ILookup to support multi-valued attributes where a single (CsoId, AttributeId) can have multiple values.
    /// Rebuilt each page via RefreshForPageAsync.
    /// </summary>
    public ILookup<(Guid CsoId, int AttributeId), ConnectedSystemObjectAttributeValue> CsoAttributeValues { get; set; }

    /// <summary>
    /// Creates a new export evaluation cache.
    /// </summary>
    /// <param name="exportRulesByMvoTypeId">Export rules grouped by MVO type ID.</param>
    /// <param name="csoLookup">CSO lookup dictionary (empty for deferred per-page loading).</param>
    /// <param name="csoAttributeValues">CSO attribute values lookup (empty for deferred per-page loading).</param>
    /// <param name="targetSystemIds">Target connected system IDs for per-page CSO queries.</param>
    public ExportEvaluationCache(
        Dictionary<int, List<SyncRule>> exportRulesByMvoTypeId,
        Dictionary<(Guid MvoId, int ConnectedSystemId), ConnectedSystemObject> csoLookup,
        ILookup<(Guid CsoId, int AttributeId), ConnectedSystemObjectAttributeValue> csoAttributeValues,
        IReadOnlyList<int> targetSystemIds)
    {
        ExportRulesByMvoTypeId = exportRulesByMvoTypeId;
        CsoLookup = csoLookup;
        CsoAttributeValues = csoAttributeValues;
        TargetSystemIds = targetSystemIds;
    }
}
