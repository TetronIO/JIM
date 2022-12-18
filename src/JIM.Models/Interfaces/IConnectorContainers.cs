using JIM.Models.Staging;

namespace JIM.Models.Interfaces
{
    public interface IConnectorContainers
    {
        /// <summary>
        /// Gets the top-level container for a connected system, that the Connector is authorised to read.
        /// The container may/is likely to contain child containers to represent a hierarchy of containers.
        /// </summary>
        public ConnectorContainer? GetContainers();
    }
}
