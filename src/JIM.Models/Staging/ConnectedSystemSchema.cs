namespace JIM.Models.Staging
{
    public class ConnectedSystemSchema
    {
        public List<ConnectedSystemSchemaObjectType> ObjectTypes { get; set; }

        public ConnectedSystemSchema()
        {
            ObjectTypes = new List<ConnectedSystemSchemaObjectType>();
        }
    }
}
