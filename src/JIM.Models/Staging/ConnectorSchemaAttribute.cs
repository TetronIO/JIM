using JIM.Models.Core;

namespace JIM.Models.Staging
{
    public class ConnectorSchemaAttribute
    {
        public string Name { get; set; }
        public AttributeDataType Type { get; set; }
        public AttributePlurality AttributePlurality { get; set; }

        public ConnectorSchemaAttribute(string name, AttributeDataType type, AttributePlurality attributePlurality)
        {
            Name = name;
            Type = type;
            AttributePlurality = attributePlurality;
        }
    }
}
