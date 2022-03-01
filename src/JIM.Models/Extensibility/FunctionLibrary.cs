namespace JIM.Models.Extensibility
{
    /// <summary>
    /// Represents a collection of functions, provided in the form of a .NET dll file.
    /// </summary>
    public class FunctionLibrary
    {
        public int Id { get; set; }
        public string Filename { get; set; }
        public string Version { get; set; }
        public DateTime Created { get; set; }
        public DateTime LastUpdated { get; set; }

        // todo: signing info?

        public FunctionLibrary()
        {
        }
    }
}
