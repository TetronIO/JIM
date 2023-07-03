namespace JIM.Models.Exceptions
{
    public class MissingUniqueIdentifierAttributeException : Exception
    {
        public MissingUniqueIdentifierAttributeException(string message) : base(message) { }
    }
}
