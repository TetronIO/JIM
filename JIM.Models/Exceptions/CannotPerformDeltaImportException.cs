namespace JIM.Models.Exceptions;

public class CannotPerformDeltaImportException : OperationalException
{
    public CannotPerformDeltaImportException(string message) : base(message) { }
}