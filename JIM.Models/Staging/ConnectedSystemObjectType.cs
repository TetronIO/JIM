namespace JIM.Models.Staging
{
    public class ConnectedSystemObjectType
    {
        public int Id { get; set; }

        public string Name { get; set; } = null!;

        public DateTime Created { get; set; } = DateTime.UtcNow;

        public ConnectedSystem ConnectedSystem { get; set; } = null!;

        public List<ConnectedSystemObjectTypeAttribute> Attributes { get; set; } = new();

        /// <summary>
        /// Whether or not an administrator has selected this object type to be synchronised by JIM.
        /// </summary>
        public bool Selected { get; set; }
    }
}
