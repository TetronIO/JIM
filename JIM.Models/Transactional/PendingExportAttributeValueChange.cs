using JIM.Models.Staging;
namespace JIM.Models.Transactional;

public class PendingExportAttributeValueChange
{
    public Guid Id { get; set; }

    public ConnectedSystemObjectTypeAttribute Attribute { get; set; } = null!;
    public int AttributeId { get; set; }

    public string? StringValue { get; set; }

    public DateTime? DateTimeValue { get; set; }

    public int? IntValue { get; set; }

    public byte[]? ByteValue { get; set; }
    
    /// <summary>
    /// Contains the unique identifier that the connected system uses to refer to references in string form.
    /// </summary>
    public string? UnresolvedReferenceValue { get; set; }

    public PendingExportAttributeChangeType ChangeType { get; set; }
}