namespace JIM.Models.Exceptions;

public class DuplicatePendingExportException : OperationalException
{
    public DuplicatePendingExportException(string message) : base(message) { }
}
