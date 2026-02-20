namespace JIM.Models.Staging;

public class ConnectorDefinitionFile
{
    public int Id { get; set; }

    public ConnectorDefinition ConnectorDefinition { get; set; } = null!;

    public string Filename { get; set; } = null!;

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

    public byte[] File { get; set; } = null!;

    public string Version { get; set; } = null!;
}