namespace JIM.Models.Exceptions;

public class MissingExternalIdAttributeException : Exception
{
    public MissingExternalIdAttributeException(string message) : base(message) { }
}