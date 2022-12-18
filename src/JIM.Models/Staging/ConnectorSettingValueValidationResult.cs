namespace JIM.Models.Staging
{
    public class ConnectorSettingValueValidationResult
    {
        public ConnectedSystemSetting Setting { get; set; }
        public bool IsValid { get; set; }
        public string? SuccessMessage { get; set; }
        public string? ErrorMessage { get; set; }

        public ConnectorSettingValueValidationResult(ConnectedSystemSetting setting, bool isValid, string? successMessage, string? errorMessage)
        {
            Setting = setting;
            IsValid = isValid;
            SuccessMessage = successMessage;
            ErrorMessage = errorMessage;
        }
    }
}
