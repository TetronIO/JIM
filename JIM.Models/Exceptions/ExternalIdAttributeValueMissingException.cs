namespace JIM.Models.Exceptions;

public class ExternalIdAttributeValueMissingException : Exception
{
    public ExternalIdAttributeValueMissingException(string message) : base(message) { }
}