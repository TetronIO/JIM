namespace JIM.Models.Interfaces
{
    /// <summary>
    /// Defines how a Connector can let JIM know what capabilities it supports.
    /// </summary>
    public interface IConnectorCapabilities
    {
        public bool SupportsImport { get; set; }
        public bool SupportsDeltaImport { get; set; }
        public bool SupportsExport { get; set; }
    }
}
