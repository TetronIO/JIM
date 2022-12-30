namespace JIM.Models.Staging
{
    /// <summary>
    /// WIP. Needs incorporating.
    /// </summary>
    public class ConnectorSettingValueValidationResult
    {
        public ConnectedSystemSettingValue Setting { get; set; }
        public bool IsValid { get; set; }
        public string? SuccessMessage { get; set; }
        public string? ErrorMessage { get; set; }

        public ConnectorSettingValueValidationResult(ConnectedSystemSettingValue setting, bool isValid, string? successMessage, string? errorMessage)
        {
            Setting = setting;
            IsValid = isValid;
            SuccessMessage = successMessage;
            ErrorMessage = errorMessage;
        }
    }
}
