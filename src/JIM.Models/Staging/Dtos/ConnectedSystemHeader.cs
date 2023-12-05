namespace JIM.Models.Staging.DTOs
{
    public class ConnectedSystemHeader
    {
        public int Id { get; set; }

        public string Name { get; set; }

        public string? Description { get; set; }

        public int ObjectCount { get; set; }

        public int ConnectorsCount { get; set; }

        public int PendingExportObjectsCount { get; set; }

        public string ConnectorName { get; set; } = null!;

        public int ConnectorId { get; set; }

        public override string ToString() => Name;
    }
}
