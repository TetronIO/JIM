namespace JIM.Models.Exceptions;

public class DuplicateAttributesException : OperationalException
{
    public DuplicateAttributesException(string message) : base(message) { }
}