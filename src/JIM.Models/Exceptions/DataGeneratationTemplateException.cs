namespace JIM.Models.Exceptions
{
    public class DataGeneratationTemplateException : Exception
    {
        public DataGeneratationTemplateException(string message) : base(message)
        {
        }

        public DataGeneratationTemplateException(string message, Exception inner) : base(message, inner)
        {
        }
    }
}
