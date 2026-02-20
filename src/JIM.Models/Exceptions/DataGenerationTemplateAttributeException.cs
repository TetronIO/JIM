namespace JIM.Models.Exceptions
{
    public class DataGenerationTemplateAttributeException : OperationalException
    {
        public DataGenerationTemplateAttributeException(string message) : base(message)
        {
        }

        public DataGenerationTemplateAttributeException(string message, Exception inner) : base(message, inner)
        {
        }
    }
}
