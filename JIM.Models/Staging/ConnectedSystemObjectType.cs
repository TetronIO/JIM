using JIM.Models.Logic;
namespace JIM.Models.Staging;

public class ConnectedSystemObjectType
{
    public int Id { get; set; }

    public string Name { get; set; } = null!;

    public DateTime Created { get; set; } = DateTime.UtcNow;

    public ConnectedSystem ConnectedSystem { get; set; } = null!;
    public int ConnectedSystemId { get; set; }

    public List<ConnectedSystemObjectTypeAttribute> Attributes { get; set; } = new();

    /// <summary>
    /// Whether an administrator has selected this object type to be managed by JIM.
    /// </summary>
    public bool Selected { get; set; }

    /// <summary>
    /// Controls whether Metaverse Object attribute values contributed by a Connected System Object of this type
    /// should be removed when the CSO is obsoleted. When true, attributes contributed by the CSO
    /// will be added to PendingAttributeValueRemovals. When false, attributes remain on the MVO.
    /// </summary>
    public bool RemoveContributedAttributesOnObsoletion { get; set; } = true;

    /// <summary>
    /// Object matching rules for this object type. Used when the Connected System's ObjectMatchingRuleMode
    /// is set to ConnectedSystem (the default). These rules are shared across all sync rules for this object type.
    /// </summary>
    public List<ObjectMatchingRule> ObjectMatchingRules { get; set; } = new();

    public override string ToString()
    {
        return Name;
    }
}