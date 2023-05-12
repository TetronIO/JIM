namespace JIM.Models.Staging
{
    /// <summary>
    /// Represents an object that's just been imported from a Connected System. 
    /// Trying a simple form for now, to make life easy for Connector developers. They will then be able to pass
    /// these simple objects back to JIM, which will handle the more complex task of matching with Connector Space
    /// objects and Pending Export objects and process them further, i.e. synchronising values.
    /// </summary>
    public class ConnectedSystemImportObject
    {
        /// <summary>
        /// The value for the attribute that uniquely identifies this object in the connected system.
        /// It should be immutable (not change for the lifetime of the object). 
        /// The connected system may author it, or you may specify it at provisioning time. It depends on the system.
        /// </summary>
        public string? UniqueIdentifierAttributeValue { get; set; }
        
        public string? ObjectType { get; set; }

        public List<ConnectedSystemImportObjectAttribute> Attributes { get; set; } = new List<ConnectedSystemImportObjectAttribute>();

        public ConnectedSystemImportObjectChangeType ChangeType { get; set; } = ConnectedSystemImportObjectChangeType.NotSet;
    }
}