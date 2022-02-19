using TIM.Models.Staging;

namespace TIM.Models.Core
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
