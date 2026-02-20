using JIM.Models.Core;
using JIM.Models.Search;
using JIM.Models.Staging;
namespace JIM.Models.Logic;

public class SyncRuleScopingCriteria
{
    public int Id { get; set; }

    /// <summary>
    /// The Metaverse Attribute to evaluate for outbound (export) sync rules.
    /// One of MetaverseAttribute or ConnectedSystemAttribute must be set.
    /// </summary>
    public MetaverseAttribute? MetaverseAttribute { get; set; }

    /// <summary>
    /// The Connected System Object Type Attribute to evaluate for inbound (import) sync rules.
    /// One of MetaverseAttribute or ConnectedSystemAttribute must be set.
    /// </summary>
    public ConnectedSystemObjectTypeAttribute? ConnectedSystemAttribute { get; set; }

    public SearchComparisonType ComparisonType { get; set; } = SearchComparisonType.NotSet;

    public string? StringValue { get; set; }

    public int? IntValue { get; set; }

    public long? LongValue { get; set; }

    public DateTime? DateTimeValue { get; set; }

    public bool? BoolValue { get; set; }

    public Guid? GuidValue { get; set; }

    /// <summary>
    /// When true (default), value comparisons are case-sensitive.
    /// When false, comparisons ignore case differences.
    /// Only applies to text/string comparisons.
    /// </summary>
    public bool CaseSensitive { get; set; } = true;

    /// <summary>
    /// Gets the data type of the attribute being evaluated (from either MV or CS attribute).
    /// </summary>
    public AttributeDataType? GetAttributeDataType()
    {
        if (MetaverseAttribute != null)
            return MetaverseAttribute.Type;
        if (ConnectedSystemAttribute != null)
            return ConnectedSystemAttribute.Type;
        return null;
    }

    /// <summary>
    /// Gets the name of the attribute being evaluated (from either MV or CS attribute).
    /// </summary>
    public string? GetAttributeName()
    {
        if (MetaverseAttribute != null)
            return MetaverseAttribute.Name;
        if (ConnectedSystemAttribute != null)
            return ConnectedSystemAttribute.Name;
        return null;
    }

    public override string ToString()
    {
        var attributeType = GetAttributeDataType();
        if (attributeType == null)
            return "No attribute set";

        return attributeType switch
        {
            AttributeDataType.Text => "Text: " + StringValue,
            AttributeDataType.Number => "Number: " + IntValue,
            AttributeDataType.LongNumber => "LongNumber: " + LongValue,
            AttributeDataType.Boolean => "Boolean: " + (BoolValue is null ? "Null" : BoolValue.Value.ToString()),
            AttributeDataType.DateTime => "Date: " + DateTimeValue,
            AttributeDataType.Guid => "Guid: " + GuidValue,
            _ => "Unsupported data type"
        };
    }
}