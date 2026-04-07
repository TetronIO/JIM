using JIM.Models.Core.DTOs;

namespace JIM.Application.Exceptions;

/// <summary>
/// Thrown when an operation on a metaverse attribute is blocked because the attribute
/// is still in use (has stored values or is referenced by sync rule configurations).
/// </summary>
public class MetaverseAttributeInUseException : InvalidOperationException
{
    /// <summary>
    /// Sync rules that reference the attribute (via mappings, matching rules, or scoping criteria).
    /// Empty if the exception is about stored values rather than sync rule references.
    /// </summary>
    public List<SyncRuleReference> ReferencingSyncRules { get; }

    /// <summary>
    /// The number of metaverse objects affected, if the exception relates to stored attribute values.
    /// Null if the exception is about sync rule references.
    /// </summary>
    public int? AffectedObjectCount { get; }

    public MetaverseAttributeInUseException(string message, List<SyncRuleReference> referencingSyncRules)
        : base(message)
    {
        ReferencingSyncRules = referencingSyncRules;
        AffectedObjectCount = null;
    }

    public MetaverseAttributeInUseException(string message, int affectedObjectCount)
        : base(message)
    {
        ReferencingSyncRules = [];
        AffectedObjectCount = affectedObjectCount;
    }
}
