namespace JIM.Models.Exceptions;

public class ExampleDataTemplateException : OperationalException
{
    public ExampleDataTemplateException(string message) : base(message)
    {
    }

    public ExampleDataTemplateException(string message, Exception inner) : base(message, inner)
    {
    }
}