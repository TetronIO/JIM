namespace JIM.Models.Staging
{
    public class ConnectedSystemObjectType
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public DateTime Created { get; set; }
        public ConnectedSystem ConnectedSystem { get; set; }
        public List<ConnectedSystemAttribute> Attributes { get; set; }

        /// <summary>
        /// Whether or not an administrator has selected this object type to be synchronised by JIM.
        /// </summary>
        public bool Selected { get; set; }

        /// <summary>
        /// The user chosen attribute to use as the unique identifier attribute.
        /// Typically this is guided by the connected system though, and the connected system can make recommendations on what attributes to use.
        /// </summary>
        public ConnectedSystemAttribute? UniqueIdentifierAttribute { get; set; }

        public ConnectedSystemObjectType()
        {
            Created = DateTime.Now;
            Attributes = new List<ConnectedSystemAttribute>();
        }
    }
}
