namespace JIM.Models.Exceptions;

public class MissingExternalIdAttributeException : OperationalException
{
    public MissingExternalIdAttributeException(string message) : base(message) { }
}