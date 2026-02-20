namespace JIM.Models.Exceptions;

public class DataGenerationTemplateException : OperationalException
{
    public DataGenerationTemplateException(string message) : base(message)
    {
    }

    public DataGenerationTemplateException(string message, Exception inner) : base(message, inner)
    {
    }
}