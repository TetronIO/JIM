namespace JIM.Models.Staging
{
    /// <summary>
    /// Defines an instance of a connector definition setting that JIM will ask an administrator to supply a value for when configuring a Connected System.
    /// </summary>
    public class ConnectedSystemSettingValue
    {
        public int Id { get; set; }
        
        public ConnectedSystem ConnectedSystem { get; set; } = null!;

        public ConnectorDefinitionSetting Setting { get; set; } = null!;

        public string? StringValue { get; set; }
        
        public string? StringEncryptedValue { get; set; }

        public int? IntValue { get; set; }
        
        public bool CheckboxValue { get; set; }

        public bool IsValueValid()
        {
            // at least one value is required for the object to be valid
            return StringValue != null || StringEncryptedValue != null || IntValue.HasValue;
        }
    }
}
