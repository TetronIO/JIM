using JIM.Models.Staging;
using Microsoft.EntityFrameworkCore;
namespace JIM.Models.Core;

[Index(nameof(StringValue))]
[Index(nameof(ReferenceValue))]
[Index(nameof(DateTimeValue))]
[Index(nameof(IntValue))]
[Index(nameof(LongValue))]
[Index(nameof(ReferenceValue))]
[Index(nameof(UnresolvedReferenceValue))]
[Index(nameof(GuidValue))]
public class MetaverseObjectAttributeValue
{
    public Guid Id { get; set; }
    public MetaverseAttribute Attribute { get; set; } = null!;
    public int AttributeId { get; set; }
    public MetaverseObject MetaverseObject { get; set; } = null!;
    public string? StringValue { get; set; }
    public DateTime? DateTimeValue { get; set; }
    public int? IntValue { get; set; }
    public long? LongValue { get; set; }
    public byte[]? ByteValue { get; set; }

    /// <summary>
    /// A reference to another Metaverse Object. Used for attributes like 'Manager' and 'Member'.
    /// </summary>
    public MetaverseObject? ReferenceValue { get; set; }
    public Guid? ReferenceValueId { get; set; }

    /// <summary>
    /// When wanting to set a ReferenceValue, the referenced MVO may not yet exist as it might not have been projected from a CS to the MV yet.
    /// In this situation, the reference should be staged by setting a reference to the projecting CSO here. The sync processor can then run through these at
    /// the end of a sync run when all MVOs have been projected, and convert the UnresolvedReferenceValue to a ReferenceValue.
    /// </summary>
    public ConnectedSystemObject? UnresolvedReferenceValue { get; set; }
    public Guid? UnresolvedReferenceValueId { get; set; }

    public Guid? GuidValue { get; set; }
    public bool? BoolValue { get; set; }

    /// <summary>
    /// If this attribute value was contributed to the Metaverse by a connected system, then this identifies that system.
    /// </summary>
    public ConnectedSystem? ContributedBySystem { get; set; }

    public override string ToString()
    {
        var output = "";
        if (Attribute != null)
            output += $"{Attribute.Name}: ";

        if (!string.IsNullOrEmpty(StringValue))
        {
            output += StringValue;
            return output;
        }

        if (DateTimeValue != null)
        {
            output += DateTimeValue.ToString();
            return output;
        }

        if (IntValue != null)
        {
            output += IntValue.ToString();
            return output;
        }

        if (LongValue != null)
        {
            output += LongValue.ToString();
            return output;
        }

        if (ByteValue != null)
        {
            output += ByteValue.Length.ToString();
            return output;
        }

        if (GuidValue.HasValue)
        {
            output += GuidValue.Value.ToString();
            return output;
        }

        if (BoolValue.HasValue)
        {
            output += BoolValue.Value.ToString();
            return output;
        }

        if (ReferenceValue != null)
        {
            output += $"{ReferenceValue.Id} ({ReferenceValue.DisplayName})";
            return output;
        }

        return string.Empty;
    }
}