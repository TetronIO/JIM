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
}
