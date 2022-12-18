namespace JIM.Models.Staging
{
    /// <summary>
    /// Defines a setting that a Connector will ask the administrator to supply a value for. 
    /// JIM will discover these when inspecting a Connector and create internal copies of it's own, that it will then reference in ConnectedSystemSettingValue objects.
    /// </summary>
    public class ConnectorSetting
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public ConnectedSystemSettingCategory Category { get; set; }
        public ConnectedSystemSettingType Type { get; set; }
        public bool? DefaultCheckboxValue { get; set; }
        public string? DefaultStringValue { get; set; }
        public List<string>? DropDownValues { get; set; }

        public ConnectorSetting() { }

        public ConnectorSetting(ConnectedSystemSettingCategory category, ConnectedSystemSettingType type)
        {
            Category = category;
            Type = type;
        }

        public ConnectorSetting(string name, ConnectedSystemSettingCategory category, ConnectedSystemSettingType type)
        {
            Name = name;
            Category = category;
            Type = type;
        }

        public ConnectorSetting(string name, string? description, ConnectedSystemSettingCategory category, ConnectedSystemSettingType type)
        {
            Name = name;
            Description = description;
            Category = category;
            Type = type;
        }

        public ConnectorSetting(string name, string? description, string defaultStringValue, ConnectedSystemSettingCategory category, ConnectedSystemSettingType type)
        {
            Name = name;
            Description = description;
            DefaultStringValue = defaultStringValue;
            Category = category;
            Type = type;
        }

        public ConnectorSetting(string name, bool defaultCheckboxValue, ConnectedSystemSettingCategory category, ConnectedSystemSettingType type)
        {
            Name = name;
            DefaultCheckboxValue = defaultCheckboxValue;
            Category = category;
            Type = type;
        }

        public ConnectorSetting(string name, string? description, bool defaultCheckboxValue, ConnectedSystemSettingCategory category, ConnectedSystemSettingType type)
        {
            Name = name;
            Description = description;
            DefaultCheckboxValue = defaultCheckboxValue;
            Category = category;
            Type = type;
        }
    }
}
