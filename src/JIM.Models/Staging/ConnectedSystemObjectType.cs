namespace JIM.Models.Staging
{
    public class ConnectedSystemObjectType
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public DateTime Created { get; set; }
        public ConnectedSystem ConnectedSystem { get; set; }

        public ConnectedSystemObjectType()
        {
            Created = DateTime.Now;
        }
    }
}
