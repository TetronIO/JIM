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
    /// Gets the current mode setting value for the Connected System.
    /// Returns null if no Mode setting exists (connector doesn't support modes).
    /// </summary>
    public static string? GetMode(this ConnectedSystem connectedSystem)
    {
        return connectedSystem.SettingValues
            .FirstOrDefault(sv => sv.Setting?.Name == ModeSettingName)?.StringValue;
    }

    /// <summary>
    /// Determines whether the Connected System is in Export Only mode.
    /// Returns false if the connector doesn't have a Mode setting.
    /// </summary>
    public static bool IsExportOnlyMode(this ConnectedSystem connectedSystem)
    {
        return connectedSystem.GetMode() == ModeExportOnly;
    }

    /// <summary>
    /// Determines whether the Connected System supports import operations
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
    /// Determines whether the Connected System supports export operations
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
    /// Returns <c>false</c> otherwise, indicating that Run Profiles cannot be executed.
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
    /// Returns the partitions a Run Profile execution may read from: the partition the Run Profile targets when it
    /// targets one, otherwise every selected partition. Either way only selected partitions are returned.
    /// </summary>
    /// <remarks>
    /// A partition's <see cref="ConnectedSystemPartition.Selected"/> flag is the administrator's statement of what
    /// JIM manages, and it binds regardless of how a Run Profile is pointed. Targeting used to bypass the flag, so
    /// deselecting a partition was a no-op for a Run Profile that named it and a mass obsoletion for one that did
    /// not; the same tick meant opposite things depending on which Run Profile ran next. Every caller that decides
    /// what to read must come through here so that cannot diverge again.
    /// </remarks>
    public static IEnumerable<ConnectedSystemPartition> GetTargetPartitions(
        this ConnectedSystem connectedSystem,
        ConnectedSystemRunProfile runProfile)
    {
        ArgumentNullException.ThrowIfNull(connectedSystem);
        ArgumentNullException.ThrowIfNull(runProfile);

        if (connectedSystem.Partitions == null)
            return [];

        if (runProfile.Partition == null)
            return connectedSystem.Partitions.Where(p => p.Selected);

        // Resolve the targeted partition against the Connected System's own hierarchy rather than trusting the
        // Run Profile's copy: the hierarchy is what a refresh updates, and the Run Profile may reference a partition
        // that has since been deselected or removed from the directory.
        return connectedSystem.Partitions.Where(p => p.Id == runProfile.Partition.Id && p.Selected);
    }

    /// <summary>
    /// Determines whether a Run Profile is left inoperable by the current partition selections: it targets a
    /// partition that is deselected, or one the hierarchy no longer carries.
    /// </summary>
    /// <remarks>
    /// A Run Profile that targets no partition is never inoperable by this measure; it follows whatever is selected.
    /// </remarks>
    public static bool TargetsADeselectedPartition(
        this ConnectedSystem connectedSystem,
        ConnectedSystemRunProfile runProfile)
    {
        ArgumentNullException.ThrowIfNull(connectedSystem);
        ArgumentNullException.ThrowIfNull(runProfile);

        if (runProfile.Partition == null)
            return false;

        return !connectedSystem.GetTargetPartitions(runProfile).Any();
    }

    /// <summary>
    /// Determines whether a Run Profile targets a partition that is no longer selected, using only the Run Profile
    /// and its loaded <see cref="ConnectedSystemRunProfile.Partition"/> navigation.
    /// </summary>
    /// <remarks>
    /// This is the form for surfaces that list Run Profiles and hold no Connected System hierarchy: the REST DTO,
    /// the portal's Run Profiles tab, and PowerShell. It answers the same question as the
    /// <see cref="TargetsADeselectedPartition(ConnectedSystem, ConnectedSystemRunProfile)"/> overload for the case
    /// they can see, and it cannot detect a partition dropped from the hierarchy altogether; prefer the overload
    /// wherever the Connected System is already in hand, as the Run Profile execution gate does.
    /// Requires the <see cref="ConnectedSystemRunProfile.Partition"/> navigation to have been loaded; callers that
    /// project Run Profiles without it will always read <c>false</c>.
    /// </remarks>
    public static bool TargetsADeselectedPartition(this ConnectedSystemRunProfile runProfile)
    {
        ArgumentNullException.ThrowIfNull(runProfile);

        return runProfile.Partition is { Selected: false };
    }

    /// <summary>
    /// Collects the external ids of every container the Connected System manages, walking the whole hierarchy
    /// beneath every selected partition.
    /// </summary>
    /// <remarks>
    /// This is the scope JIM manages, and it answers two questions that must not diverge: where a rights check
    /// should be run, and where an export is permitted to write. Selecting a container selects its subtree, so a
    /// caller comparing against this list should treat a descendant identifier as in scope.
    /// </remarks>
    public static List<string> GetSelectedContainerExternalIds(this ConnectedSystem connectedSystem)
    {
        ArgumentNullException.ThrowIfNull(connectedSystem);

        var selected = new List<string>();
        if (connectedSystem.Partitions == null)
            return selected;

        foreach (var container in connectedSystem.Partitions
                     .Where(p => p.Selected && p.Containers != null)
                     .SelectMany(p => p.Containers!))
            CollectSelectedContainers(container, selected);

        return selected;
    }

    private static void CollectSelectedContainers(ConnectedSystemContainer container, List<string> selected)
    {
        if (container.Selected && !string.IsNullOrEmpty(container.ExternalId))
            selected.Add(container.ExternalId);

        foreach (var child in container.ChildContainers)
            CollectSelectedContainers(child, selected);
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
