using JIM.Models.Staging;

namespace JIM.Models.Interfaces
{
    /// <summary>
    /// Enables a Connector to return a list of Partitions within a Connected System, i.e. LDAP partitions.
    /// </summary>
    public interface IConnectorPartitions
    {
        public IList<ConnectorPartition> GetPartitions(IList<ConnectedSystemSetting> settings);
    }
}
