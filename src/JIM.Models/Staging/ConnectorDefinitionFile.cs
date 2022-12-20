namespace JIM.Models.Staging
{
    public class ConnectorDefinitionFile
    {
        public int Id { get; set; }
        public ConnectorDefinition ConnectorDefinition { get; set; }
        public string Filename { get; set; }
        public bool ImplementsIConnector { get; set; }
        public bool ImplementsICapabilities { get; set; }
        public bool ImplementsISchema { get; set; }
        public bool ImplementsISettings { get; set; }
        public bool ImplementsIContainers { get; set; }
        public bool ImplementsIExportUsingCalls { get; set; }
        public bool ImplementsIExportUsingFiles { get; set; }
        public bool ImplementsIImportUsingCalls { get; set; }
        public bool ImplementsIImportUsingFiles { get; set; }
        public int FileSizeBytes { get; set; }
    }
}
