namespace JIM.Models.Exceptions;

public class DataGenerationTemplateException : Exception
{
    public DataGenerationTemplateException(string message) : base(message)
    {
    }

    public DataGenerationTemplateException(string message, Exception inner) : base(message, inner)
    {
    }
}