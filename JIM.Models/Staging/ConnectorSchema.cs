namespace JIM.Models.Staging
{
    public class ConnectorSchema
    {
        public List<ConnectorSchemaObjectType> ObjectTypes { get; set; }

        public ConnectorSchema()
        {
            ObjectTypes = new List<ConnectorSchemaObjectType>();
        }
    }
}
