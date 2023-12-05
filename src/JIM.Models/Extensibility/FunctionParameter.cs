namespace JIM.Models.Extensibility
{
    public class FunctionParameter
    {
        public int Id { get; set; }

        public Function Function { get; set; } = null!;

        public string Name { get; set; } = null!;

        public int Position { get; set; }

        public FunctionParameterType Type { get; set; }

        public FunctionParameter()
        {
        }
    }
}
