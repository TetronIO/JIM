namespace JIM.Models.Extensibility
{
    public class FunctionParameter
    {
        public Guid Id { get; set; }
        public Function Function { get; set; }
        public string Name { get; set; }
        public int Position { get; set; }
        public FunctionParameterType Type { get; set; }

        public FunctionParameter()
        {
        }
    }
}
