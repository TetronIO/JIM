namespace JIM.Application.Diagnostics;

/// <summary>
/// Provides pre-configured diagnostic sources for different areas of JIM.
/// Use these static sources to instrument operations throughout the codebase.
/// </summary>
public static class Diagnostics
{
    /// <summary>
    /// Diagnostic source for synchronisation operations (import, sync, export).
    /// </summary>
    public static DiagnosticSource Sync { get; } = new("JIM.Sync");

    /// <summary>
    /// Diagnostic source for database/repository operations.
    /// </summary>
    public static DiagnosticSource Database { get; } = new("JIM.Database");

    /// <summary>
    /// Diagnostic source for connector operations (LDAP, File, Database connectors).
    /// </summary>
    public static DiagnosticSource Connector { get; } = new("JIM.Connector");

    /// <summary>
    /// Diagnostic source for expression evaluation operations.
    /// </summary>
    public static DiagnosticSource Expression { get; } = new("JIM.Expression");

    /// <summary>
    /// Initialises the diagnostic listener for logging span completions.
    /// Call this once during application startup.
    /// </summary>
    /// <param name="slowOperationThresholdMs">Operations taking longer than this will be logged at Warning level. Default is 1000ms.</param>
    /// <returns>The diagnostic listener instance. Keep a reference to prevent garbage collection.</returns>
    public static DiagnosticListener EnableLogging(double slowOperationThresholdMs = 1000)
    {
        return new DiagnosticListener(
            sourcePrefix: "JIM.",
            logLevel: Serilog.Events.LogEventLevel.Information,
            slowOperationThresholdMs: slowOperationThresholdMs);
    }
}
