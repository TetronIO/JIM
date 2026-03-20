using JIM.Models.Logic;
using JIM.Models.Staging;

namespace JIM.Models.Transactional;

/// <summary>
/// Cache class for pre-loaded export evaluation data.
/// Pass this to the optimised evaluation methods to avoid O(N×M) database queries.
/// </summary>
public class ExportEvaluationCache
{
    /// <summary>
    /// Pre-loaded export rules, keyed by MVO type ID.
    /// </summary>
    public Dictionary<int, List<SyncRule>> ExportRulesByMvoTypeId { get; }

    /// <summary>
    /// Pre-loaded CSO lookup, keyed by (MvoId, ConnectedSystemId).
    /// </summary>
    public Dictionary<(Guid MvoId, int ConnectedSystemId), ConnectedSystemObject> CsoLookup { get; }

    /// <summary>
    /// Pre-loaded target CSO attribute values for no-net-change detection during export evaluation.
    /// Uses ILookup to support multi-valued attributes where a single (CsoId, AttributeId) can have multiple values.
    /// </summary>
    public ILookup<(Guid CsoId, int AttributeId), ConnectedSystemObjectAttributeValue> CsoAttributeValues { get; }

    /// <summary>
    /// Creates a new export evaluation cache with pre-loaded data.
    /// </summary>
    public ExportEvaluationCache(
        Dictionary<int, List<SyncRule>> exportRulesByMvoTypeId,
        Dictionary<(Guid MvoId, int ConnectedSystemId), ConnectedSystemObject> csoLookup,
        ILookup<(Guid CsoId, int AttributeId), ConnectedSystemObjectAttributeValue> csoAttributeValues)
    {
        ExportRulesByMvoTypeId = exportRulesByMvoTypeId;
        CsoLookup = csoLookup;
        CsoAttributeValues = csoAttributeValues;
    }
}
