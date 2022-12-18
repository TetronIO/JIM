namespace JIM.Models.Staging
{
    /// <summary>
    /// Defines a setting that a Connector will ask the administrator to supply a value for. 
    /// Values for the setting are modelled by the ConnectorSettingValue object.
    /// </summary>
    public class ConnectedSystemSetting
    {
        public int Id { get; set; }
        public string? Name { get; set; }
        public string? Description { get; set; }
        public ConnectedSystemSettingCategory Category { get; set; }
        public ConnectedSystemSettingType Type { get; set; }
        public bool DefaultCheckboxValue { get; }
        public string DefaultStringValue { get; }
        public List<string> DropDownValues { get; }
        public string LabelValue { get; set; }

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
