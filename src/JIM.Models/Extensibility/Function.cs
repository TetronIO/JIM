namespace JIM.Models.Extensibility
{
    public class Function
    {
        public int Id { get; set; }

        public FunctionLibrary FunctionLibrary { get; set; } = null!;

        public string Name { get; set; } = null!;

        public string? Description { get; set; }

        public FunctionOutputType OutputType { get; set; }
    }
}
