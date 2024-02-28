namespace JIM.Models.Exceptions
{
    public class DataGeneratationTemplateAttributeException : Exception
    {
        public DataGeneratationTemplateAttributeException(string message) : base(message)
        {
        }

        public DataGeneratationTemplateAttributeException(string message, Exception inner) : base(message, inner)
        {
        }
    }
}
