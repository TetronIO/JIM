namespace JIM.Models.Staging
{
    public class ConnectedSystemObjectType
    {
        public Guid Id { get; set; }
        public string Name { get; set; }
        public DateTime Created { get; set; }
        public ConnectedSystem ConnectedSystem { get; set; }

        public ConnectedSystemObjectType(ConnectedSystem connectedSystem, string name)
        {
            ConnectedSystem = connectedSystem;
            Name = name;
            Created = DateTime.Now;
        }
    }
}
