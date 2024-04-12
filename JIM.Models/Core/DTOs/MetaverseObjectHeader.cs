namespace JIM.Models.Core.DTOs;

public class MetaverseObjectHeader
{
    public Guid Id { get; set; }

    public DateTime Created { get; set; }

    public string TypeName { get; set; } = null!;

    public int TypeId { get; set; }

    public List<MetaverseObjectAttributeValue> AttributeValues { get; set; } = new();

    public MetaverseObjectStatus Status { get; set; }

    public string? DisplayName 
    { 
        get
        {
            if (AttributeValues.Count == 0)
                return null;

            var av = AttributeValues.SingleOrDefault(q => q.Attribute.Name == Constants.BuiltInAttributes.DisplayName);
            if (av != null && ! string.IsNullOrEmpty(av.StringValue))
                return av.StringValue;

            return null;
        } 
    }

    public MetaverseObjectAttributeValue? GetAttributeValue(string name)
    {
        return AttributeValues.SingleOrDefault(q => q.Attribute.Name == name);
    }

    public bool HasAttributeValue(string name)
    {
        return AttributeValues.Any(q => q.Attribute.Name == name);
    }
}