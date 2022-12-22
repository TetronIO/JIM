using Microsoft.Extensions.Primitives;

namespace JIM.Models.Staging
{
    /// <summary>
    /// Defines a setting that a Connector will ask an administrator to supply a value for. 
    /// </summary>
    public class ConnectedSystemSetting
    {
        public int Id { get; set; }
        
        public ConnectedSystem ConnectedSystem { get; set; }

        public string Name { get; set; }

        public string Description { get; set; }

        public ConnectedSystemSettingCategory Category { get; set; }

        public ConnectedSystemSettingType Type { get; set; }

        public string? StringValue { get; set; }
        
        public string? StringEncryptedValue { get; set; }
        
        public bool? CheckboxValue { get; set; }

        public bool IsValueValid()
        {
            // at least one value is required for the object to be valid
            return StringValue != null || StringEncryptedValue != null || CheckboxValue != null;
        }

    }
}
