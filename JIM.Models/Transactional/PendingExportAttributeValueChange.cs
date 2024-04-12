using JIM.Models.Staging;
namespace JIM.Models.Transactional;

public class PendingExportAttributeValueChange
{
    public Guid Id { get; set; }

    public ConnectedSystemObjectTypeAttribute Attribute { get; set; } = null!;

    public string? StringValue { get; set; }

    public DateTime? DateTimeValue { get; set; }

    public int? IntValue { get; set; }

    public byte[]? ByteValue { get; set; }

    public PendingExportAttributeChangeType ChangeType { get; set; }

    /// <summary>
    /// How many times have we encountered an error whilst trying to export this attribute value change to the connected system?
    /// </summary>
    public int? ErrorCount { get; set; }
}