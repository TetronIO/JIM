namespace JIM.Models.Exceptions;

public class ExternalIdAttributeValueMissingException : OperationalException
{
    public ExternalIdAttributeValueMissingException(string message) : base(message) { }
}