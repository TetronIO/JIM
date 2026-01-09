namespace JIM.Models.Interfaces;

/// <summary>
/// Interface for connectors that support container creation and hierarchy operations.
/// Implementing this interface allows JIM to track newly created containers,
/// verify their existence, and navigate container hierarchies without
/// connector-specific knowledge in the application layer.
/// </summary>
public interface IConnectorContainerCreation
{
    /// <summary>
    /// Gets the list of container external IDs that were created during the current export session.
    /// This list should be populated during Export() and cleared on CloseExportConnection().
    /// </summary>
    IReadOnlyList<string> CreatedContainerExternalIds { get; }

    /// <summary>
    /// Verifies that a container exists in the connected system.
    /// Implementations should perform a lightweight, targeted check rather than
    /// fetching the entire container hierarchy.
    /// </summary>
    /// <remarks>
    /// This method assumes an export connection is already open. Implementations
    /// should use the existing connection context established by OpenExportConnection().
    /// </remarks>
    /// <param name="containerExternalId">The container's external identifier.</param>
    /// <returns>True if the container exists, false otherwise.</returns>
    Task<bool> VerifyContainerExistsAsync(string containerExternalId);

    /// <summary>
    /// Gets the parent container's external ID from a child container's external ID.
    /// The connector is responsible for understanding its own identifier format.
    /// </summary>
    /// <param name="containerExternalId">The child container's external identifier.</param>
    /// <returns>The parent container's external ID, or null if at root level.</returns>
    string? GetParentContainerExternalId(string containerExternalId);

    /// <summary>
    /// Extracts a human-readable display name from a container's external ID.
    /// </summary>
    /// <param name="containerExternalId">The container's external identifier.</param>
    /// <returns>A display-friendly name for the container.</returns>
    string GetContainerDisplayName(string containerExternalId);
}
