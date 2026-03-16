namespace JIM.Models.Exceptions
{
    public class ExampleDataTemplateAttributeException : OperationalException
    {
        public ExampleDataTemplateAttributeException(string message) : base(message)
        {
        }

        public ExampleDataTemplateAttributeException(string message, Exception inner) : base(message, inner)
        {
        }
    }
}
