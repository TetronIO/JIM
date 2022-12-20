namespace JIM.Models.Staging
{
    /// <summary>
    /// Defines a setting that a Connector will ask an administrator to supply a value for. 
    /// Values for the setting are modelled by the ConnectorSettingValue object.
    /// Inherits from ConnectorSetting. 
    /// Needs to be a dedicated class so we can persist to the database with id, connected system and value references.
    /// </summary>
    public class ConnectedSystemSetting : ConnectorSetting
    {
        public int Id { get; set; }
        public ConnectedSystem ConnectedSystem { get; set; }
        public ConnectedSystemSettingValue? Value { get; set; }

        public ConnectedSystemSetting() { }

        public ConnectedSystemSetting(ConnectedSystemSettingCategory category, ConnectedSystemSettingType type)
        {
            Category = category;
            Type = type;
        }

        public ConnectedSystemSetting(string name, ConnectedSystemSettingCategory category, ConnectedSystemSettingType type)
        {
            Name = name;
            Category = category;
            Type = type;
        }

        public ConnectedSystemSetting(string name, string? description, ConnectedSystemSettingCategory category, ConnectedSystemSettingType type)
        {
            Name = name;
            Description = description;
            Category = category;
            Type = type;
        }

        public ConnectedSystemSetting(string name, string? description, string defaultStringValue, ConnectedSystemSettingCategory category, ConnectedSystemSettingType type)
        {
            Name = name;
            Description = description;
            DefaultStringValue = defaultStringValue;
            Category = category;
            Type = type;
        }

        public ConnectedSystemSetting(string name, bool defaultCheckboxValue, ConnectedSystemSettingCategory category, ConnectedSystemSettingType type)
        {
            Name = name;
            DefaultCheckboxValue = defaultCheckboxValue;
            Category = category;
            Type = type;
        }

        public ConnectedSystemSetting(string name, string? description, bool defaultCheckboxValue, ConnectedSystemSettingCategory category, ConnectedSystemSettingType type)
        {
            Name = name;
            Description = description;
            DefaultCheckboxValue = defaultCheckboxValue;
            Category = category;
            Type = type;
        }
    }
}
