using JIM.Models.Core;

namespace JIM.Models.Staging
{
    public class ConnectedSystemAttribute : BaseAttribute
    {
        public ConnectedSystem ConnectedSystem { get; set; }

        public ConnectedSystemAttribute(ConnectedSystem connectedSystem, string name) : base(name)
        {
            ConnectedSystem = connectedSystem;
        }
    }
}
