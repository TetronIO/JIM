using JIM.Models.Staging;

namespace JIM.Models.Interfaces
{
    /// <summary>
    /// Defines how a connector can provide settings to JIM that are needed to make it work for a specific environment, 
    /// i.e. connection strings, credentials, paths, etc.
    /// </summary>
    public interface IConnectorSettings
    {
        /// <summary>
        /// Returns a list of setting definitions that the Connector offers, i.e. connection and general configuration settings.
        /// </summary>
        public IList<ConnectorSetting> GetSettings();

        /// <summary>
        /// Validates that all required combinations of settings values have been provided by an administrator, and optionally if the values themselves are valid, i.e. can the system be contacted with the supplied credentials.
        /// </summary>
        /// <returns>Whether or not the setting values are valid.</returns>
        public IList<ConnectorSettingValueValidationResult> ValidateSettingValues(IList<ConnectedSystemSettingValue> settings);
    }
}