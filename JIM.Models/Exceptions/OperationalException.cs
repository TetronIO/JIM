namespace JIM.Models.Exceptions;

/// <summary>
/// Base class for expected, user-actionable operational exceptions.
///
/// Operational exceptions represent known failure modes with clear causes and remediation steps,
/// such as missing configuration, failed prerequisite checks, or data validation errors.
///
/// When an OperationalException reaches the Worker catch-all, only the error message is persisted
/// to the Activity - the stack trace is omitted because it provides no diagnostic value and
/// confuses administrators. Unexpected exceptions (bugs) continue to include full stack traces
/// for developer diagnosis.
/// </summary>
public class OperationalException : Exception
{
    public OperationalException(string message) : base(message) { }

    public OperationalException(string message, Exception? innerException)
        : base(message, innerException) { }
}
