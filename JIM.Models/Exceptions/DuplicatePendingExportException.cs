namespace JIM.Models.Exceptions;

public class DuplicatePendingExportException : Exception
{
    public DuplicatePendingExportException(string message) : base(message) { }
}
