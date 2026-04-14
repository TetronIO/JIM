// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// Extension methods for ConnectedSystem to provide helper functionality
/// for common operations like mode checking.
/// </summary>
public static class ConnectedSystemExtensions
{
    // Mode setting constants (matching FileConnector definitions)
    private const string ModeSettingName = "Mode";
    private const string ModeImportOnly = "Import Only";
    private const string ModeExportOnly = "Export Only";
    private const string ModeBidirectional = "Bidirectional";

    /// <summary>
    /// Gets the current mode setting value for the connected system.
    /// Returns null if no Mode setting exists (connector doesn't support modes).
    /// </summary>
    public static string? GetMode(this ConnectedSystem connectedSystem)
    {
        return connectedSystem.SettingValues
            .FirstOrDefault(sv => sv.Setting?.Name == ModeSettingName)?.StringValue;
    }

    /// <summary>
    /// Determines whether the connected system is in Export Only mode.
    /// Returns false if the connector doesn't have a Mode setting.
    /// </summary>
    public static bool IsExportOnlyMode(this ConnectedSystem connectedSystem)
    {
        return connectedSystem.GetMode() == ModeExportOnly;
    }

    /// <summary>
    /// Determines whether the connected system supports import operations
    /// based on its mode setting. Returns true for Import Only and Bidirectional modes,
    /// or if the connector doesn't have a Mode setting.
    /// </summary>
    public static bool SupportsImportMode(this ConnectedSystem connectedSystem)
    {
        var mode = connectedSystem.GetMode();

        // If no mode setting, assume import is supported (most connectors)
        if (mode == null)
            return true;

        return mode == ModeImportOnly || mode == ModeBidirectional;
    }

    /// <summary>
    /// Determines whether the connected system supports export operations
    /// based on its mode setting. Returns true for Export Only and Bidirectional modes,
    /// or if the connector doesn't have a Mode setting.
    /// </summary>
    public static bool SupportsExportMode(this ConnectedSystem connectedSystem)
    {
        var mode = connectedSystem.GetMode();

        // If no mode setting, assume export is supported (most connectors)
        if (mode == null)
            return true;

        return mode == ModeExportOnly || mode == ModeBidirectional;
    }

    /// <summary>
    /// Determines if a Connected System has the required partition and container selections for synchronisation.
    /// </summary>
    /// <remarks>
    /// Returns <c>true</c> if:
    /// <list type="bullet">
    ///   <item>The connector doesn't support partitions (no selection needed)</item>
    ///   <item>At least one partition is selected AND (if containers are supported) at least one container is selected within a selected partition</item>
    /// </list>
    /// Returns <c>false</c> otherwise, indicating that run profiles cannot be executed.
    /// </remarks>
    /// <param name="connectedSystem">The Connected System to check.</param>
    /// <returns><c>true</c> if the system has valid partition/container selections or doesn't require them; otherwise <c>false</c>.</returns>
    public static bool HasPartitionsOrContainersSelected(this ConnectedSystem connectedSystem)
    {
        ArgumentNullException.ThrowIfNull(connectedSystem);
        ArgumentNullException.ThrowIfNull(connectedSystem.ConnectorDefinition);

        // If the connector doesn't support partitions, no selection is needed
        if (!connectedSystem.ConnectorDefinition.SupportsPartitions)
            return true;

        // If partitions are supported but none have been retrieved, return false
        if (connectedSystem.Partitions == null || connectedSystem.Partitions.Count == 0)
            return false;

        // Check if any partition is selected
        var selectedPartitions = connectedSystem.Partitions.Where(p => p.Selected).ToList();
        if (selectedPartitions.Count == 0)
            return false;

        // If the connector doesn't support containers, having selected partitions is sufficient
        if (!connectedSystem.ConnectorDefinition.SupportsPartitionContainers)
            return true;

        // If containers are supported, at least one container must be selected within any selected partition
        return selectedPartitions.Any(partition =>
            partition.Containers != null &&
            HasAnySelectedContainers(partition.Containers));
    }

    /// <summary>
    /// Recursively checks if any containers in the collection (or their children) are selected.
    /// </summary>
    private static bool HasAnySelectedContainers(IEnumerable<ConnectedSystemContainer> containers)
    {
        foreach (var container in containers)
        {
            if (container.Selected)
                return true;

            if (container.ChildContainers.Count > 0 && HasAnySelectedContainers(container.ChildContainers))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Produces a diagnostic description of why <see cref="HasPartitionsOrContainersSelected"/> would return <c>false</c>,
    /// suitable for embedding in user-facing error and warning messages.
    /// </summary>
    /// <remarks>
    /// The returned string identifies which stage of partition configuration is incomplete:
    /// the hierarchy has not been imported, partitions have been enumerated but none are selected,
    /// or a partition is selected but no container within it is. Callers are expected to have already
    /// determined that selection is incomplete; the return value for a valid configuration is undefined.
    /// </remarks>
    public static string BuildPartitionSelectionDiagnostic(this ConnectedSystem connectedSystem)
    {
        ArgumentNullException.ThrowIfNull(connectedSystem);

        var partitions = connectedSystem.Partitions;
        if (partitions == null || partitions.Count == 0)
            return "the partition hierarchy has not been imported: run a partition hierarchy import before selecting partitions";

        var selectedPartitions = partitions.Where(p => p.Selected).ToList();
        if (selectedPartitions.Count == 0)
            return $"{partitions.Count} partition(s) are available but none are selected";

        var supportsContainers = connectedSystem.ConnectorDefinition?.SupportsPartitionContainers ?? false;
        if (!supportsContainers)
        {
            // HasPartitionsOrContainersSelected would have returned true in this path; retain a sensible fallback.
            return $"{selectedPartitions.Count} partition(s) are selected but validation still reports the configuration as incomplete";
        }

        var selectedNames = string.Join(", ", selectedPartitions.Select(p => $"'{p.Name}'"));
        var totalContainers = selectedPartitions.Sum(p => p.Containers != null ? CountContainersRecursively(p.Containers) : 0);
        if (totalContainers == 0)
            return $"selected partition(s) {selectedNames} contain no enumerated containers: import the partition hierarchy to populate containers";

        return $"{totalContainers} container(s) exist under selected partition(s) {selectedNames} but none are selected";
    }

    /// <summary>
    /// Counts every container in the tree rooted at the supplied collection, including nested descendants.
    /// </summary>
    private static int CountContainersRecursively(IEnumerable<ConnectedSystemContainer> containers)
    {
        var count = 0;
        foreach (var container in containers)
        {
            count++;
            if (container.ChildContainers.Count > 0)
                count += CountContainersRecursively(container.ChildContainers);
        }
        return count;
    }
}
