namespace JIM.Models.Staging.DTOs
{
    public class ConnectorDefinitionHeader
    {
        public int Id { get; set; }

        public string Name { get; set; } = null!;

        public string? Description { get; set; }

        public bool BuiltIn { get; set; }

        public DateTime Created { get; set; }

        public DateTime? LastUpdated { get; set; }

        public int Files { get; set; }

        public string? Versions { get; set; }

        public bool InUse { get; set; }
    }
}
