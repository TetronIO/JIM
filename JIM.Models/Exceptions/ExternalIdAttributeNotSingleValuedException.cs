namespace JIM.Models.Exceptions;

public class ExternalIdAttributeNotSingleValuedException : OperationalException
{
    public ExternalIdAttributeNotSingleValuedException(string message) : base(message) { }
}