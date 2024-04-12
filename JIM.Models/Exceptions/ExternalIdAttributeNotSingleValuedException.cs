namespace JIM.Models.Exceptions;

public class ExternalIdAttributeNotSingleValuedException : Exception
{
    public ExternalIdAttributeNotSingleValuedException(string message) : base(message) { }
}