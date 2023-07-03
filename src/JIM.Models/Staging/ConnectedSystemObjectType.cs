namespace JIM.Models.Staging
{
    public class ConnectedSystemObjectType
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public DateTime Created { get; set; }
        public ConnectedSystem ConnectedSystem { get; set; }
        public List<ConnectedSystemObjectTypeAttribute> Attributes { get; set; }

        /// <summary>
        /// Whether or not an administrator has selected this object type to be synchronised by JIM.
        /// </summary>
        public bool Selected { get; set; }

        public ConnectedSystemObjectType()
        {
            Created = DateTime.Now;
            Attributes = new List<ConnectedSystemObjectTypeAttribute>();
        }
    }
}
