namespace JIM.Models.Interfaces;

/// <summary>
/// Interface for connectors that can create containers (OUs) during export operations.
/// Implementing this interface allows JIM to track and auto-select newly created containers.
/// </summary>
public interface IConnectorContainerCreation
{
    /// <summary>
    /// Gets the list of container DNs that were created during the current export session.
    /// This list should be populated during Export() and cleared on CloseExportConnection().
    /// </summary>
    IReadOnlyList<string> CreatedContainerDns { get; }
}
