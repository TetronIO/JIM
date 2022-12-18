using JIM.Models.Staging;

namespace JIM.Models.Interfaces
{
    /// <summary>
    /// Defines how a connector can provide settings to JIM that are needed to make it work for a specific environment, 
    /// i.e. connection strings, credentials, paths, etc.
    /// </summary>
    public interface IConnectorSettings
    {
        public IList<ConnectedSystemSetting> GetSettings();
    }
}