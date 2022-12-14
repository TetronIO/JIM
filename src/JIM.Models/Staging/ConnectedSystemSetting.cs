namespace JIM.Models.Staging
{
    /// <summary>
    /// Defines a setting that a Connector will ask the administrator to supply a value for. 
    /// Values for the setting are modelled by the ConnectorSettingValue object.
    /// </summary>
    public class ConnectedSystemSetting
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string? Description { get; set; }
        public ConnectedSystemSettingCategory Category { get; set; }
        public ConnectedSystemSettingType Type { get; set; }

    }
}
