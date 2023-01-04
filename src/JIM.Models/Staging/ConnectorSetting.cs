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
        public bool Required { get; set; }
    }
}
