namespace JIM.Models.Staging
{
    /// <summary>
    /// Represents the result of validating a setting value.
    /// Can refer to a specific setting value, or generally to all setting values, i.e. in the case of dependent or aggregates setting value requirements.
    /// </summary>
    /// <remarks>If not valid, do supply an error message so that the user can be informed of how to resolve the issue.</remarks>
    public class ConnectorSettingValueValidationResult
    {
        public ConnectedSystemSettingValue? SettingValue { get; set; }
        
        public bool IsValid { get; set; }

        public string? ErrorMessage { get; set; }

        /// <summary>
        /// If an unhandled exception was encountered whilst validating the setting values, it can be made available here.
        /// </summary>
        public Exception? Exception { get; set; }
    }
}
