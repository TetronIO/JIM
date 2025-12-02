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

    public override string ToString()
    {
        return Name;
    }
}