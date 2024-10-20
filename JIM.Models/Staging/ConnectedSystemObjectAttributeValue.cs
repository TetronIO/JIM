using System.ComponentModel.DataAnnotations.Schema;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
namespace JIM.Models.Staging;

public class ConnectedSystemObjectAttributeValue
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    /// <summary>
    /// The parent attribute for this attribute value object.
    /// </summary>
    public ConnectedSystemObjectTypeAttribute Attribute { get; set; } = null!;
    public int AttributeId { get; set; }

    /// <summary>
    /// The parent connected system object for this attribute value object.
    /// </summary>
    public ConnectedSystemObject ConnectedSystemObject { get; set; } = null!;

    public string? StringValue { get; set; }

    public DateTime? DateTimeValue { get; set; }

    public int? IntValue { get; set; }

    public byte[]? ByteValue { get; set; }

    public Guid? GuidValue { get; set; }

    public bool? BoolValue { get; set; }

    /// <summary>
    /// This holds a link to the referenced object immediately after provisioning from the Metaverse, or after unresolved references are resolved at the end of imports.
    /// Termed as a hard reference, as the soft reference will have been resolved to a Connected System Object as part of setting this value.
    /// </summary>
    public ConnectedSystemObject? ReferenceValue { get; set; }
    public Guid? ReferenceValueId { get; set; }

    /// <summary>
    /// This holds the soft (aka raw) reference value from the Connected System before it gets resolved into a hard reference to another Connected System Object as part of an Import operation.
    /// </summary>
    public string? UnresolvedReferenceValue { get; set; }

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

        if (ReferenceValue != null && !string.IsNullOrEmpty(UnresolvedReferenceValue))
        {
            output += $"Resolved: {ReferenceValue.Id}. Unresolved: {UnresolvedReferenceValue}";
            return output;
        }

        if (ReferenceValue != null)
        {
            output += $"Resolved: " + ReferenceValue.Id;
            return output;
        }

        if (!string.IsNullOrEmpty(UnresolvedReferenceValue))
        {
            output += $"Unresolved: " + UnresolvedReferenceValue;
            return output;
        }

        return string.Empty;
    }
}