namespace JIM.Models.Staging
{
    public class ConnectorSchemaObjectType
    {
        /// <summary>
        /// The name of the Object Type in the connected system.
        /// Recommend using standard conventions, i.e. "User" or "Group" to simplify mappings between the metaverse and connected system object types and enable JIM to auto-map them.
        /// </summary>
        public string Name { get; set; }

        public List<ConnectorSchemaAttribute> Attributes { get; set; }

        /// <summary>
        /// Which attribute(s) for this object type, are recommended to uniquely identify the object in the connected system?
        /// The recommended attribute(s) should be immutable for the lifetime of the object so that JIM can always identify it and not see connected system objects as being deleted or created when unique ids change in the connected system.
        /// Generally, it's best to use system-generated attribute(s) for this where appropriate, rather than using business-generated attribute(s) as system-generated attributes are less likely to change value over time.
        /// </summary>
        public List<ConnectorSchemaAttribute> RecommendedUniqueIdentifierAttributes { get; set; }

        public ConnectorSchemaObjectType(string name)
        {
            Name = name;
            Attributes = new List<ConnectorSchemaAttribute>();
            RecommendedUniqueIdentifierAttributes = new List<ConnectorSchemaAttribute>();
        }
    }
}
