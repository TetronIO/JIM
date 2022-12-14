using JIM.Models.Core;

namespace JIM.Models.Staging
{
    public class ConnectedSystemSchemaAttribute
    {
        public string Name { get; set; }
        public AttributeDataType Type { get; set; }
        public AttributePlurality AttributePlurality { get; set; }

        public ConnectedSystemSchemaAttribute(string name, AttributeDataType type, AttributePlurality attributePlurality)
        {
            Name = name;
            Type = type;
            AttributePlurality = attributePlurality;
        }
    }
}
