using System.Security;

namespace JIM.Models.Staging
{
    /// <summary>
    /// Models a value for a connector setting, i.e. the admin has entered a connection string to use with a Connector.
    /// </summary>
    public class ConnectedSystemSettingValue
    {
        public int Id { get; set; }
        public ConnectedSystemSetting ConnectedSystemSetting { get; set; }
        public string? StringValue { get; set; }
        public SecureString? StringEncryptedValue { get; set; }
        public bool CheckboxValue { get; set; }
    }
}
