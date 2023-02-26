using System.ComponentModel.DataAnnotations;

namespace JIM.Web.Models
{
    public class ConnectedSystemCreateViewModel
    {
        public string ConnectorId { get; set; }

        public string Name { get; set; }

        public string? Description { get; set; }
    }
}
