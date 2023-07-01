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
        /// The user chosen attribute(s) to use as the way to uniquely identify the object type in its source system.
        /// Typically this is guided by the connected system though, and the connected system can make recommendations on what attribute(s) to use.
        /// Whilst it's most common to just use a single attribute, it's possible to use multiple, in a compound primary key scenario.
        /// </summary>
        public List<ConnectedSystemAttribute> UniqueIdentifierAttributes { get; set; }

        public ConnectedSystemObjectType()
        {
            Created = DateTime.Now;
            Attributes = new List<ConnectedSystemAttribute>();
            UniqueIdentifierAttributes = new List<ConnectedSystemAttribute>();
        }
    }
}
