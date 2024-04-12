using JIM.Models.Core;
using JIM.Models.Search;
namespace JIM.Models.Logic;

public class SyncRuleScopingCriteria
{
    public int Id { get; set; }

    public MetaverseAttribute MetaverseAttribute { get; set; } = null!;

    public SearchComparisonType ComparisonType { get; set; } = SearchComparisonType.NotSet;

    public string? StringValue { get; set; }

    public int? IntValue { get; set; }

    public DateTime? DateTimeValue { get; set; }

    public bool? BoolValue { get; set; }

    public Guid? GuidValue {  get; set; }

    public override string ToString()
    {
        switch (MetaverseAttribute.Type)
        {
            case AttributeDataType.Text:
                return "Text: " + StringValue;
            case AttributeDataType.Number:
                return "Number: " + StringValue;
            case AttributeDataType.Boolean:
                return "Boolean: " + (BoolValue is null ? "Null" : BoolValue.Value.ToString());
            case AttributeDataType.DateTime:
                return "Date: " + DateTimeValue;
            case AttributeDataType.Guid:
                return "Guid: " + GuidValue;
        }

        return "Unsupported data type";
    }
}