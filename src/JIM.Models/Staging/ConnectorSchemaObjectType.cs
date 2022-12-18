namespace JIM.Models.Staging
{
    public class ConnectorSchemaObjectType
    {
        /// <summary>
        /// The name of the Object Type in the connected system.
        /// Recommend using standard conventions, i.e. "User" or "Group" to simplify attribute mappings and enable JIM to auto-map object types.
        /// </summary>
        public string Name { get; set; }

        public List<ConnectorSchemaAttribute> Attributes { get; set; }

        /// <summary>
        /// Which attribute for this object type, is recommended to uniquely identify the object in the connected system?
        /// This should be immutable for the lifetime of the object so that JIM can always identify it and not see connected system objects as being deleted or created when unique ids change in the connected system.
        /// Generally, it's best to use a system-generated attribute for this if appropriate, rather than a business-generated attribute as system-generated attributes are less likely to change value over time.
        /// </summary>
        public ConnectorSchemaAttribute? RecommendedUniqueIdentifierAttribute { get; set; }

        public ConnectorSchemaObjectType(string name)
        {
            Name = name;
            Attributes = new List<ConnectorSchemaAttribute>();
        }
    }
}
