using System.ComponentModel.DataAnnotations;

namespace JIM.Web.Models
{
    public class ConnectedSystemCreateViewModel
    {
        [Required(ErrorMessage = "Please select a connector")]
        public string ConnectorId { get; set; }

        [Required(ErrorMessage = "Please provide a name for the new connected system")]
        public string Name { get; set; }

        public string Description { get; set; }
    }
}
