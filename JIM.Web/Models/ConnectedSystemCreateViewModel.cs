namespace JIM.Web.Models
{
    public class ConnectedSystemCreateViewModel
    {
        public string ConnectorId { get; set; } = null!;

        public string Name { get; set; } = null!;

        public string? Description { get; set; }
    }
}
