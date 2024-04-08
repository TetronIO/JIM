namespace JIM.Models.Exceptions
{
    public class DataGenerationTemplateAttributeException : Exception
    {
        public DataGenerationTemplateAttributeException(string message) : base(message)
        {
        }

        public DataGenerationTemplateAttributeException(string message, Exception inner) : base(message, inner)
        {
        }
    }
}
