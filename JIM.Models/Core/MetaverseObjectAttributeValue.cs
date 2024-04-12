using JIM.Models.Staging;
using Microsoft.EntityFrameworkCore;
namespace JIM.Models.Core;

[Index(nameof(StringValue))]
[Index(nameof(ReferenceValue))]
[Index(nameof(DateTimeValue))]
[Index(nameof(IntValue))]
[Index(nameof(ReferenceValue))]
[Index(nameof(GuidValue))]
public class MetaverseObjectAttributeValue
{
    public Guid Id { get; set; }
    public MetaverseAttribute Attribute { get; set; } = null!;
    public MetaverseObject MetaverseObject { get; set; } = null!;
    public string? StringValue { get; set; }
    public DateTime? DateTimeValue { get; set; }
    public int? IntValue { get; set; }
    public byte[]? ByteValue { get; set; }
    public MetaverseObject? ReferenceValue { get; set; }
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