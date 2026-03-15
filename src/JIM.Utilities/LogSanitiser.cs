namespace JIM.Utilities;

/// <summary>
/// Provides sanitisation of user-controlled strings before they are written to log entries,
/// preventing log injection attacks (CWE-117 / OWASP Log Injection).
/// </summary>
/// <remarks>
/// All user-controlled string values MUST be wrapped with <see cref="Sanitise"/> before being
/// passed as arguments to any ILogger or Serilog log call. This is mandatory for all JIM projects.
/// Integers, GUIDs, enums, DateTimes, and other non-string types do not require sanitisation.
/// </remarks>
public static class LogSanitiser
{
    /// <summary>
    /// Removes newline characters from a user-controlled string to prevent log injection.
    /// Returns null if the input is null.
    /// </summary>
    public static string? Sanitise(string? value) => value?.ReplaceLineEndings(string.Empty);
}
