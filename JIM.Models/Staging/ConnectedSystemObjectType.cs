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
}