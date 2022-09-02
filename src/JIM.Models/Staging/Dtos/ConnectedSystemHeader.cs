namespace JIM.Models.Staging.Dtos
{
    public class ConnectedSystemHeader
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string? Description { get; set; }
        public int ObjectCount { get; set; }
        public int ConnectorsCount { get; set; }
        public int PendingExportObjectsCount { get; set; }

    }
}
